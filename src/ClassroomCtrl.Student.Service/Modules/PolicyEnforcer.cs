using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;

namespace ClassroomCtrl.Student.Service.Modules;

/// <summary>
/// Security policy enforcement (Spec §6.11) — Override model with time-bound auto-revert.
/// 
/// Two policy layers:
///   _classPolicy       — applied via broadcast (TargetEndpointId == Empty)
///   _perStudentPolicy  — applied via targeted message (TargetEndpointId == myId)
/// 
/// Effective policy = Merge(class, perStudent):
///   bool fields: OR
///   string lists: union
/// 
/// Time-bound: each layer can have ExpiresAtUtcMs set; a Timer auto-reverts when fired.
/// </summary>
public class PolicyEnforcer : IDisposable
{
    private const string UsbStorPath = @"SYSTEM\CurrentControlSet\Services\USBSTOR";
    private const string CdRomPath = @"SYSTEM\CurrentControlSet\Services\CdRom";
    private const string StartValue = "Start";
    private const int UsbDefault = 3, UsbBlocked = 4;
    private const int CdDefault = 1, CdBlocked = 4;

    private static readonly string HostsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "drivers", "etc", "hosts");
    private static readonly string HostsBackup = HostsPath + ".classroomctrl.bak";
    private const string HostsBlockMarkerStart = "# === ClassroomCtrl block START ===";
    private const string HostsBlockMarkerEnd = "# === ClassroomCtrl block END ===";

    private readonly ILogger<PolicyEnforcer> _logger;

    // Two policy layers
    private PolicyApplyMessage? _classPolicy;
    private PolicyApplyMessage? _perStudentPolicy;

    // Timers for time-bound auto-revert
    private Timer? _classExpiryTimer;
    private Timer? _perStudentExpiryTimer;

    // Currently applied effective state (for revert)
    private PolicyApplyMessage? _appliedEffective;
    private int? _savedUsbStartValue;
    private int? _savedCdStartValue;
    private string? _savedSpoolerStartType;
    private bool _spoolerWasRunning;
    private Timer? _processKillTimer;
    private bool _hostsBlockApplied;

    public PolicyEnforcer(ILogger<PolicyEnforcer> logger) => _logger = logger;

    // ─────── Public API ───────

    public void ApplyClass(PolicyApplyMessage p)
    {
        _logger.LogInformation("Class policy: USB={Usb} CD={Cd} Print={Print} Apps={Apps} Hosts={Hosts} ExpiresAt={Exp}",
            p.BlockUsbStorage, p.BlockOpticalDrive, p.BlockPrinting,
            p.BlockedProcessNames.Count, p.BlockedHostnames.Count,
            p.ExpiresAtUtcMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.ExpiresAtUtcMs).ToString("HH:mm:ss") : "never");
        _classPolicy = p;
        ScheduleClassExpiry(p.ExpiresAtUtcMs);
        Rebuild();
    }

    public void RevertClass()
    {
        _logger.LogInformation("Class policy: REVERT");
        _classPolicy = null;
        _classExpiryTimer?.Dispose();
        _classExpiryTimer = null;
        Rebuild();
    }

    public void ApplyPerStudent(PolicyApplyMessage p)
    {
        _logger.LogInformation("Per-student policy: USB={Usb} CD={Cd} Print={Print} Apps={Apps} Hosts={Hosts} ExpiresAt={Exp}",
            p.BlockUsbStorage, p.BlockOpticalDrive, p.BlockPrinting,
            p.BlockedProcessNames.Count, p.BlockedHostnames.Count,
            p.ExpiresAtUtcMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(p.ExpiresAtUtcMs).ToString("HH:mm:ss") : "never");
        _perStudentPolicy = p;
        SchedulePerStudentExpiry(p.ExpiresAtUtcMs);
        Rebuild();
    }

    public void RevertPerStudent()
    {
        _logger.LogInformation("Per-student policy: REVERT");
        _perStudentPolicy = null;
        _perStudentExpiryTimer?.Dispose();
        _perStudentExpiryTimer = null;
        Rebuild();
    }

    // ─────── Time-bound expiry timers ───────

    private void ScheduleClassExpiry(long expiresAtUtcMs)
    {
        _classExpiryTimer?.Dispose();
        _classExpiryTimer = null;

        if (expiresAtUtcMs <= 0) return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var delayMs = expiresAtUtcMs - nowMs;
        if (delayMs <= 0)
        {
            _logger.LogInformation("Class policy already expired — reverting immediately");
            RevertClass();
            return;
        }

        _logger.LogInformation("Class policy will auto-revert in {Sec} seconds", delayMs / 1000);
        _classExpiryTimer = new Timer(_ =>
        {
            _logger.LogInformation("Class policy timer fired — auto-reverting");
            RevertClass();
        }, null, delayMs, Timeout.Infinite);
    }

    private void SchedulePerStudentExpiry(long expiresAtUtcMs)
    {
        _perStudentExpiryTimer?.Dispose();
        _perStudentExpiryTimer = null;

        if (expiresAtUtcMs <= 0) return;

        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var delayMs = expiresAtUtcMs - nowMs;
        if (delayMs <= 0)
        {
            _logger.LogInformation("Per-student policy already expired — reverting immediately");
            RevertPerStudent();
            return;
        }

        _logger.LogInformation("Per-student policy will auto-revert in {Sec} seconds", delayMs / 1000);
        _perStudentExpiryTimer = new Timer(_ =>
        {
            _logger.LogInformation("Per-student policy timer fired — auto-reverting");
            RevertPerStudent();
        }, null, delayMs, Timeout.Infinite);
    }

    // ─────── Effective policy compute + apply ───────

    private static PolicyApplyMessage Merge(PolicyApplyMessage? a, PolicyApplyMessage? b)
    {
        var result = new PolicyApplyMessage();
        if (a != null)
        {
            result.BlockUsbStorage   |= a.BlockUsbStorage;
            result.BlockOpticalDrive |= a.BlockOpticalDrive;
            result.BlockPrinting     |= a.BlockPrinting;
            result.BlockedProcessNames.AddRange(a.BlockedProcessNames);
            result.BlockedHostnames.AddRange(a.BlockedHostnames);
        }
        if (b != null)
        {
            result.BlockUsbStorage   |= b.BlockUsbStorage;
            result.BlockOpticalDrive |= b.BlockOpticalDrive;
            result.BlockPrinting     |= b.BlockPrinting;
            result.BlockedProcessNames.AddRange(b.BlockedProcessNames);
            result.BlockedHostnames.AddRange(b.BlockedHostnames);
        }
        // Dedupe
        result.BlockedProcessNames = result.BlockedProcessNames
            .Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.BlockedHostnames = result.BlockedHostnames
            .Select(s => s.Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    /// <summary>Revert any applied OS state, compute new effective, apply fresh.</summary>
    private void Rebuild()
    {
        // 1. Revert all currently applied
        RevertAllApplied();

        // 2. Compute effective
        var effective = Merge(_classPolicy, _perStudentPolicy);

        // 3. If effective is empty → done
        if (!effective.BlockUsbStorage && !effective.BlockOpticalDrive && !effective.BlockPrinting
            && effective.BlockedProcessNames.Count == 0 && effective.BlockedHostnames.Count == 0)
        {
            _appliedEffective = null;
            _logger.LogInformation("Effective policy: EMPTY");
            return;
        }

        // 4. Apply
        _logger.LogInformation("Effective policy: USB={Usb} CD={Cd} Print={Print} Apps={Apps} Hosts={Hosts}",
            effective.BlockUsbStorage, effective.BlockOpticalDrive, effective.BlockPrinting,
            effective.BlockedProcessNames.Count, effective.BlockedHostnames.Count);

        SaveCurrentState(effective);

        if (effective.BlockUsbStorage)        ApplyUsbStorageBlock();
        if (effective.BlockOpticalDrive)      ApplyOpticalDriveBlock();
        if (effective.BlockPrinting)          ApplyPrintBlock();
        if (effective.BlockedProcessNames.Count > 0) StartProcessKillLoop(effective.BlockedProcessNames);
        if (effective.BlockedHostnames.Count > 0)    ApplyHostsBlock(effective.BlockedHostnames);

        _appliedEffective = effective;
    }

    private void RevertAllApplied()
    {
        if (_appliedEffective == null) return;

        if (_savedUsbStartValue.HasValue) RestoreUsbStorageState(_savedUsbStartValue.Value);
        if (_savedCdStartValue.HasValue)  RestoreOpticalDriveState(_savedCdStartValue.Value);
        if (_savedSpoolerStartType != null) RestorePrintState();
        StopProcessKillLoop();
        if (_hostsBlockApplied) RevertHostsBlock();

        _appliedEffective = null;
        _savedUsbStartValue = null;
        _savedCdStartValue = null;
        _savedSpoolerStartType = null;
        _spoolerWasRunning = false;
        _hostsBlockApplied = false;
    }

    private void SaveCurrentState(PolicyApplyMessage p)
    {
        if (p.BlockUsbStorage)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(UsbStorPath);
                _savedUsbStartValue = (key?.GetValue(StartValue) as int?) ?? UsbDefault;
            }
            catch { _savedUsbStartValue = UsbDefault; }
        }

        if (p.BlockOpticalDrive)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(CdRomPath);
                _savedCdStartValue = (key?.GetValue(StartValue) as int?) ?? CdDefault;
            }
            catch { _savedCdStartValue = CdDefault; }
        }

        if (p.BlockPrinting)
        {
            try
            {
                using var sc = new ServiceController("Spooler");
                _savedSpoolerStartType = sc.StartType.ToString();
                _spoolerWasRunning = sc.Status == ServiceControllerStatus.Running;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not read Spooler state"); }
        }
    }

    // ─────── USB ───────
    private void ApplyUsbStorageBlock()    => SetRegStart(UsbStorPath, UsbBlocked, "USB Storage");
    private void RestoreUsbStorageState(int v) => SetRegStart(UsbStorPath, v, "USB Storage");

    // ─────── CD/DVD ───────
    private void ApplyOpticalDriveBlock()       => SetRegStart(CdRomPath, CdBlocked, "CD/DVD");
    private void RestoreOpticalDriveState(int v) => SetRegStart(CdRomPath, v, "CD/DVD");

    private void SetRegStart(string path, int value, string label)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (key == null) { _logger.LogWarning("{Label}: registry path not found", label); return; }
            key.SetValue(StartValue, value, RegistryValueKind.DWord);
            _logger.LogInformation("{Label} set Start={Val}", label, value);
        }
        catch (UnauthorizedAccessException)
        {
            _logger.LogError("{Label}: cannot write registry — needs admin", label);
        }
        catch (Exception ex) { _logger.LogError(ex, "{Label}: registry write failed", label); }
    }

    // ─────── Print ───────
    private void ApplyPrintBlock()
    {
        try
        {
            using var sc = new ServiceController("Spooler");
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
            }
            SetServiceStartMode("Spooler", "disabled");
            _logger.LogInformation("Print Spooler stopped & disabled");
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to stop Spooler"); }
    }

    private void RestorePrintState()
    {
        try
        {
            var mode = (_savedSpoolerStartType ?? "Automatic").ToLowerInvariant();
            var scMode = mode switch
            {
                "automatic" => "auto",
                "manual"    => "demand",
                _           => mode,
            };
            SetServiceStartMode("Spooler", scMode);

            if (_spoolerWasRunning)
            {
                using var sc = new ServiceController("Spooler");
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(10));
            }
            _logger.LogInformation("Print Spooler restored");
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to restore Spooler"); }
    }

    private static void SetServiceStartMode(string name, string scMode)
    {
        var psi = new ProcessStartInfo("sc.exe", $"config {name} start= {scMode}")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var proc = Process.Start(psi);
        proc?.WaitForExit(5000);
    }

    // ─────── Process kill ───────
    private void StartProcessKillLoop(List<string> blacklist)
    {
        var lower = blacklist.Select(s => s.Trim().ToLowerInvariant())
                             .Where(s => s.Length > 0)
                             .Select(s => s.EndsWith(".exe") ? s[..^4] : s)
                             .ToHashSet();

        if (lower.Count == 0) return;

        _processKillTimer = new Timer(_ =>
        {
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (lower.Contains(p.ProcessName.ToLowerInvariant()))
                        {
                            _logger.LogInformation("Killing blacklisted: {Name} (PID {Pid})", p.ProcessName, p.Id);
                            try { p.Kill(true); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Kill loop iteration failed"); }
        }, null, dueTime: 0, period: 2000);

        _logger.LogInformation("Process kill loop started ({Count}: {List})",
            lower.Count, string.Join(", ", lower));
    }

    private void StopProcessKillLoop()
    {
        _processKillTimer?.Dispose();
        _processKillTimer = null;
    }

    // ─────── Hosts ───────
    private void ApplyHostsBlock(List<string> hostnames)
    {
        try
        {
            if (!File.Exists(HostsBackup) && File.Exists(HostsPath))
                File.Copy(HostsPath, HostsBackup, overwrite: false);

            var lines = File.Exists(HostsPath) ? File.ReadAllLines(HostsPath).ToList() : new List<string>();
            var clean = StripClassroomBlock(lines);

            clean.Add("");
            clean.Add(HostsBlockMarkerStart);
            foreach (var h in hostnames.Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                clean.Add($"127.0.0.1 {h}");
                clean.Add($"127.0.0.1 www.{h}");
            }
            clean.Add(HostsBlockMarkerEnd);

            try { File.SetAttributes(HostsPath, FileAttributes.Normal); } catch { }
            File.WriteAllLines(HostsPath, clean);
            _hostsBlockApplied = true;

            _logger.LogInformation("Hosts file updated with {Count} domains", hostnames.Count);
            FlushDnsCache();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to apply hosts block"); }
    }

    private void RevertHostsBlock()
    {
        try
        {
            if (File.Exists(HostsBackup))
            {
                try { File.SetAttributes(HostsPath, FileAttributes.Normal); } catch { }
                File.Copy(HostsBackup, HostsPath, overwrite: true);
                File.Delete(HostsBackup);
            }
            else if (File.Exists(HostsPath))
            {
                var lines = File.ReadAllLines(HostsPath).ToList();
                var clean = StripClassroomBlock(lines);
                File.WriteAllLines(HostsPath, clean);
            }
            _logger.LogInformation("Hosts file restored");
            FlushDnsCache();
        }
        catch (Exception ex) { _logger.LogError(ex, "Failed to revert hosts"); }
    }

    private static List<string> StripClassroomBlock(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        bool inBlock = false;
        foreach (var line in lines)
        {
            if (line.Contains(HostsBlockMarkerStart)) { inBlock = true; continue; }
            if (line.Contains(HostsBlockMarkerEnd))   { inBlock = false; continue; }
            if (!inBlock) result.Add(line);
        }
        while (result.Count > 0 && string.IsNullOrWhiteSpace(result[^1]))
            result.RemoveAt(result.Count - 1);
        return result;
    }

    private static void FlushDnsCache()
    {
        try
        {
            var psi = new ProcessStartInfo("ipconfig.exe", "/flushdns")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
        }
        catch { }
    }

    public void Dispose()
    {
        RevertAllApplied();
        StopProcessKillLoop();
        _classExpiryTimer?.Dispose();
        _perStudentExpiryTimer?.Dispose();
    }
}