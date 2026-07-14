using System.Diagnostics;
using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace ClassroomCtrl.Student.Mac;

/// <summary>
/// macOS application-blocking policy enforcer — the Phase-C counterpart of the shipped
/// Windows <c>PolicyEnforcer</c> (<c>ClassroomCtrl.Student.Service/Modules/PolicyEnforcer.cs</c>),
/// ported to macOS and scoped to <b>application blocking only</b>.
///
/// <para>
/// USB-storage, optical-drive, printing, and hostname/web blocking are DELIBERATELY out of
/// scope for the MVP (they need <c>IOKit</c> / <c>pmset</c> / <c>/etc/hosts</c> + root and are
/// tracked separately). The <see cref="PolicyApplyMessage"/> still carries those flags on the
/// wire — this enforcer simply ignores them, so the Teacher's protocol is unchanged and a
/// future phase can light them up without a wire bump.
/// </para>
///
/// <para><b>Two policy layers</b> (identical semantics to Windows, so cross-platform classrooms
/// behave the same):</para>
/// <list type="bullet">
///   <item><c>_classPolicy</c> — applied via a broadcast <c>PolicyApply</c> (TargetEndpointId == Empty).</item>
///   <item><c>_perStudentPolicy</c> — applied via a targeted <c>PolicyApply</c> (TargetEndpointId == myId).</item>
/// </list>
/// <para>The effective blacklist is the <b>union</b> of both layers' <see cref="PolicyApplyMessage.BlockedProcessNames"/>.
/// Each layer may carry <see cref="PolicyApplyMessage.ExpiresAtUtcMs"/>; a <see cref="Timer"/> auto-reverts
/// that layer when it fires (the teacher can set a timed "no games for 20 minutes" block).</para>
///
/// <para><b>Enforcement</b>: a single low-CPU <see cref="Timer"/> sweeps <see cref="Process.GetProcesses"/>
/// every <see cref="PollPeriodMs"/> ms and force-terminates any process whose (normalized) name is on the
/// effective blacklist — e.g. blocking <c>Safari</c> or <c>Activity Monitor</c>. The sweep is:</para>
/// <list type="bullet">
///   <item><b>Robust</b> — every per-process access is wrapped so a vanished/protected PID can't abort the pass,
///     and killing our own daemon or a critical macOS process is refused outright.</item>
///   <item><b>Low-CPU</b> — one enumeration every 2 s with a re-entrancy guard, so a slow sweep never stacks up.</item>
///   <item><b>Idempotent</b> — re-applying the same policy just refreshes the blacklist snapshot; the timer is
///     started once and stopped only when the effective blacklist becomes empty.</item>
/// </list>
///
/// Thread-safety: all public methods and the layer/timer transitions are serialized by <see cref="_gate"/>.
/// The kill-sweep reads an atomically-swapped immutable blacklist snapshot (<see cref="_blacklist"/>) so it
/// never blocks on <see cref="_gate"/>.
/// </summary>
public sealed class MacPolicyEnforcer : IDisposable
{
    /// <summary>Kill-sweep cadence. 2 s matches the shipped Windows enforcer — responsive without burning CPU.</summary>
    public const int PollPeriodMs = 2000;

    /// <summary>
    /// Process names we refuse to terminate even if the teacher blacklists them — killing these would take the
    /// user's session or the machine down. Compared against the normalized (lower-cased, extension-stripped) name.
    /// Our own PID is excluded separately (see <see cref="Environment.ProcessId"/>).
    /// </summary>
    private static readonly HashSet<string> CriticalProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "launchd", "kernel_task", "windowserver", "loginwindow",
        "coreauthd", "securityd", "systemuiserver", "dock", "finder",
    };

    private readonly ILogger<MacPolicyEnforcer> _logger;
    private readonly object _gate = new();

    // Two policy layers (class = broadcast, per-student = targeted).
    private PolicyApplyMessage? _classPolicy;
    private PolicyApplyMessage? _perStudentPolicy;

    // Per-layer time-bound auto-revert.
    private Timer? _classExpiryTimer;
    private Timer? _perStudentExpiryTimer;

    // Enforcement state.
    private Timer? _killTimer;
    private int _sweepActive;             // re-entrancy guard for the kill sweep (0 = idle, 1 = running)
    private volatile HashSet<string> _blacklist = new(StringComparer.OrdinalIgnoreCase); // atomically swapped snapshot
    private bool _disposed;

    private readonly int _selfPid = Environment.ProcessId;

    public MacPolicyEnforcer(ILogger<MacPolicyEnforcer> logger) => _logger = logger;

    /// <summary>Snapshot of the currently-enforced blacklist (for diagnostics / UI).</summary>
    public IReadOnlyCollection<string> ActiveBlacklist => _blacklist.ToArray();

    // ─────────────────────────────────────────── Public API ───────────────────────────────────────────

    /// <summary>Apply (or replace) the class-wide policy from a broadcast <c>PolicyApply</c>.</summary>
    public void ApplyClass(PolicyApplyMessage p)
    {
        lock (_gate)
        {
            if (_disposed) return;
            LogLayer("Class", p);
            _classPolicy = p;
            ScheduleExpiry(ref _classExpiryTimer, p.ExpiresAtUtcMs, isClass: true);
            Rebuild();
        }
    }

    /// <summary>Clear the class-wide policy (broadcast <c>PolicyRevert</c>).</summary>
    public void RevertClass()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _logger.LogInformation("[Policy] Class policy REVERT");
            _classPolicy = null;
            _classExpiryTimer?.Dispose();
            _classExpiryTimer = null;
            Rebuild();
        }
    }

    /// <summary>Apply (or replace) the per-student policy from a targeted <c>PolicyApply</c>.</summary>
    public void ApplyPerStudent(PolicyApplyMessage p)
    {
        lock (_gate)
        {
            if (_disposed) return;
            LogLayer("Per-student", p);
            _perStudentPolicy = p;
            ScheduleExpiry(ref _perStudentExpiryTimer, p.ExpiresAtUtcMs, isClass: false);
            Rebuild();
        }
    }

    /// <summary>Clear the per-student policy (targeted <c>PolicyRevert</c>).</summary>
    public void RevertPerStudent()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _logger.LogInformation("[Policy] Per-student policy REVERT");
            _perStudentPolicy = null;
            _perStudentExpiryTimer?.Dispose();
            _perStudentExpiryTimer = null;
            Rebuild();
        }
    }

    // ─────────────────────────────────────── Time-bound expiry ────────────────────────────────────────

    /// <summary>
    /// (Re)arm a layer's auto-revert timer. <paramref name="expiresAtUtcMs"/> == 0 means "no expiry".
    /// An already-past expiry reverts the layer immediately. Must be called under <see cref="_gate"/>.
    /// </summary>
    private void ScheduleExpiry(ref Timer? slot, long expiresAtUtcMs, bool isClass)
    {
        slot?.Dispose();
        slot = null;
        if (expiresAtUtcMs <= 0) return;

        long delayMs = expiresAtUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (delayMs <= 0)
        {
            _logger.LogInformation("[Policy] {Layer} policy already expired — reverting immediately",
                isClass ? "Class" : "Per-student");
            // Defer the revert off the current lock frame to avoid re-entrancy on _gate.
            _ = Task.Run(() => { if (isClass) RevertClass(); else RevertPerStudent(); });
            return;
        }

        _logger.LogInformation("[Policy] {Layer} policy will auto-revert in {Sec} s",
            isClass ? "Class" : "Per-student", delayMs / 1000);

        // Clamp to Timer's max due time (~24.8 days) — defensive against a bogus far-future expiry.
        uint due = (uint)Math.Min(delayMs, int.MaxValue);
        slot = new Timer(_ =>
        {
            _logger.LogInformation("[Policy] {Layer} policy timer fired — auto-reverting",
                isClass ? "Class" : "Per-student");
            if (isClass) RevertClass(); else RevertPerStudent();
        }, null, due, Timeout.Infinite);
    }

    // ─────────────────────────────────── Effective policy compute ─────────────────────────────────────

    /// <summary>Union the two layers' blocked-process names (trimmed, de-duped, normalized). Under <see cref="_gate"/>.</summary>
    private HashSet<string> ComputeEffective()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddNames(set, _classPolicy);
        AddNames(set, _perStudentPolicy);
        return set;

        static void AddNames(HashSet<string> into, PolicyApplyMessage? p)
        {
            if (p?.BlockedProcessNames is null) return;
            foreach (var raw in p.BlockedProcessNames)
            {
                var name = Normalize(raw);
                if (name.Length > 0) into.Add(name);
            }
        }
    }

    /// <summary>
    /// Recompute the effective blacklist and start/stop the kill sweep accordingly. Under <see cref="_gate"/>.
    /// </summary>
    private void Rebuild()
    {
        var effective = ComputeEffective();
        _blacklist = effective;   // atomic reference swap — the sweep picks it up on its next tick

        if (effective.Count == 0)
        {
            StopKillSweep();
            _logger.LogInformation("[Policy] Effective app-block list is EMPTY — enforcement off");
            return;
        }

        _logger.LogInformation("[Policy] Effective app-block list ({Count}): {List}",
            effective.Count, string.Join(", ", effective));
        StartKillSweep();
    }

    // ──────────────────────────────────────── Kill sweep ──────────────────────────────────────────────

    /// <summary>Ensure the periodic kill sweep is running. Idempotent; the sweep reads the live snapshot.</summary>
    private void StartKillSweep()
    {
        if (_killTimer is not null) return;   // already sweeping — Rebuild already swapped the snapshot
        _killTimer = new Timer(KillSweep, state: null, dueTime: 0, period: PollPeriodMs);
        _logger.LogInformation("[Policy] Process kill sweep started ({Period} ms cadence)", PollPeriodMs);
    }

    private void StopKillSweep()
    {
        _killTimer?.Dispose();
        _killTimer = null;
    }

    /// <summary>
    /// One enforcement pass: enumerate processes and terminate any whose normalized name is blacklisted.
    /// Fully defensive — a vanished PID, an access-denied kill, or a slow enumeration can never throw out of
    /// the timer callback. A re-entrancy guard drops a tick if the previous pass is still running.
    /// </summary>
    private void KillSweep(object? state)
    {
        // Skip this tick if the previous sweep hasn't finished (keeps CPU bounded under process churn).
        if (Interlocked.Exchange(ref _sweepActive, 1) == 1) return;
        try
        {
            var blacklist = _blacklist;        // volatile read of the current immutable snapshot
            if (blacklist.Count == 0) return;

            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (proc.Id == _selfPid) continue;                 // never kill our own daemon

                    var name = Normalize(proc.ProcessName);
                    if (name.Length == 0 || !blacklist.Contains(name)) continue;
                    if (CriticalProcesses.Contains(name))
                    {
                        _logger.LogWarning("[Policy] Refusing to kill critical process '{Name}' (blacklist ignored for safety)", name);
                        continue;
                    }

                    _logger.LogInformation("[Policy] Terminating blacklisted app: {Name} (PID {Pid})", proc.ProcessName, proc.Id);
                    try { proc.Kill(entireProcessTree: true); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[Policy] Kill failed for {Name} (PID {Pid})", proc.ProcessName, proc.Id); }
                }
                catch { /* process exited between enumeration and inspection — ignore */ }
                finally { proc.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Policy] Kill sweep iteration failed");
        }
        finally
        {
            Interlocked.Exchange(ref _sweepActive, 0);
        }
    }

    // ──────────────────────────────────────── Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Normalize a process/app name for matching: trim, lower-case, and strip a trailing <c>.app</c> or
    /// <c>.exe</c>. This lets a teacher blacklist "Safari", "Safari.app", or the Windows-style "safari.exe"
    /// and have it match the macOS process name "Safari" — so one class-wide policy works across platforms.
    /// </summary>
    private static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var s = name.Trim();
        if (s.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        else if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s[..^4];
        return s.ToLowerInvariant();
    }

    private void LogLayer(string layer, PolicyApplyMessage p) =>
        _logger.LogInformation(
            "[Policy] {Layer} policy: Apps={Apps} (USB/CD/Print/Hosts ignored on macOS MVP) ExpiresAt={Exp}",
            layer, p.BlockedProcessNames?.Count ?? 0,
            p.ExpiresAtUtcMs > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(p.ExpiresAtUtcMs).ToLocalTime().ToString("HH:mm:ss")
                : "never");

    // ──────────────────────────────────────── IDisposable ─────────────────────────────────────────────

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            _classPolicy = null;
            _perStudentPolicy = null;
            _blacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            _classExpiryTimer?.Dispose();
            _perStudentExpiryTimer?.Dispose();
            StopKillSweep();
        }
        _logger.LogInformation("[Policy] Enforcer disposed — app blocking stopped");
    }
}
