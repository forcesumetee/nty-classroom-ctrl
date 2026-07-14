using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Teacher.Core;

/// <summary>TT-6-C — one student in a bulk operation. <see cref="CanReceivePower"/> is the
/// tile's platform gate (<see cref="StudentPlatform.CanReceivePower"/>); bulk power skips the
/// ones that can't execute it (macOS students) rather than shipping a no-op.</summary>
public sealed record BulkTarget(Guid EndpointId, bool CanReceivePower);

/// <summary>TT-6-C — the outcome of a bulk operation, for an honest, visible report:
/// <see cref="Sent"/> reached, <see cref="Skipped"/> platform-skipped (macOS + power),
/// <see cref="Cancelled"/> if the teacher declined the confirm.</summary>
public sealed record BulkResult(StudentCommand Command, int Sent, int Skipped, bool Cancelled);

/// <summary>
/// TT-5 (macOS port) — the single entry point the Teacher UI calls to issue a per-student
/// command (lock/unlock/power), mirroring TT-3's <c>ScreenViewController</c>. Promoted from
/// the Teacher app into Teacher.Core (the UI-agnostic layer) so its guarantees are protected
/// by the committed <c>--teacherselftest</c>, not a scratchpad gate. It has NO Avalonia
/// dependency — the confirm prompt is an injected <c>Func&lt;string, Task&lt;bool&gt;&gt;</c>,
/// so the Avalonia modal (ConfirmDialog) stays in the app and is wired in as this delegate.
///
/// It owns the two policies that would otherwise be easy to get wrong in the UI:
///   1. THE SEND-PATH RULE — every command is routed <c>reliable:true</c> (the never-drop
///      channel). Hardcoded here, asserted by the gate's fake sink. This is the fix for the
///      shipped v1.2.1-class lossy per-student-command bug.
///   2. CONFIRMATION — the three power actions (irreversible, expensive at 50 seats) are gated
///      behind a confirm prompt, matching the shipped Windows Teacher. Lock/unlock are
///      reversible and NOT confirmed.
///
/// Errors are swallowed-and-logged (fire-and-forget from UI event handlers): a failed send
/// must not crash the Teacher. Platform gating (power offered only to Windows students) is
/// enforced upstream at the tile/menu via <see cref="StudentPlatform"/>; this controller
/// trusts its caller for that (the LIVE gate confirms the menu state).
/// </summary>
public sealed class StudentCommandController
{
    private readonly IStudentCommandSink _sink;
    private readonly Func<string, Task<bool>> _confirmAsync;
    private readonly Action<string>? _log;

    public StudentCommandController(
        IStudentCommandSink sink,
        Func<string, Task<bool>>? confirmAsync = null,
        Action<string>? log = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        // Default: auto-confirm. Production injects the modal dialog (ConfirmDialog).
        _confirmAsync = confirmAsync ?? (_ => Task.FromResult(true));
        _log = log;
    }

    /// <summary>Dispatch a context-menu command for a student. The single surface the UI uses;
    /// fire-and-forget safe (never throws).</summary>
    public async Task ExecuteAsync(Guid endpointId, StudentCommand command, string studentName, CancellationToken ct = default)
    {
        try
        {
            switch (command)
            {
                case StudentCommand.Lock:
                    await _sink.LockAsync(endpointId, locked: true, reliable: true, ct);
                    break;
                case StudentCommand.Unlock:
                    await _sink.LockAsync(endpointId, locked: false, reliable: true, ct);
                    break;
                case StudentCommand.Logoff:
                case StudentCommand.Restart:
                case StudentCommand.Shutdown:
                    var type = ToMessageType(command);
                    if (!await _confirmAsync(ConfirmPrompt(command, studentName)))
                        return;   // teacher declined — nothing sent
                    await _sink.PowerAsync(endpointId, type, reliable: true, ct);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, "Unknown student command");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Command {command} → {endpointId} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// TT-6-C — apply a command to a SELECTION, fanning out per-student on the reliable
    /// channel (so the promoted guard covers bulk exactly as it covers a single command — a
    /// bulk command cannot go lossy without bypassing this method). Mirrors the shipped bulk
    /// loop (snapshot → sequential awaited reliable sends → "i of N" progress).
    ///
    ///   • Decision A — bulk POWER skips students that can't execute it (macOS): it applies to
    ///     the Windows ones and reports the skip count. Lock/unlock apply to everyone.
    ///   • Decision B — bulk power confirms ONCE (count-aware), even for a single student.
    ///
    /// Per-student failures are logged, not fatal (one unreachable student mustn't abort the
    /// batch). Sequential-awaited on purpose (matches shipped): correctness over speed, paced
    /// by the reliable channel; the caller shows "Sending i of N" because it isn't instant.
    /// </summary>
    public async Task<BulkResult> ExecuteBulkAsync(
        IReadOnlyList<BulkTarget> targets,
        StudentCommand command,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        bool isPower = command is StudentCommand.Logoff or StudentCommand.Restart or StudentCommand.Shutdown;

        // Decision A: power skips non-Windows students; lock/unlock apply to all.
        var eligible = isPower ? targets.Where(t => t.CanReceivePower).ToList() : targets.ToList();
        int skipped = targets.Count - eligible.Count;

        if (eligible.Count == 0)
            return new BulkResult(command, Sent: 0, Skipped: skipped, Cancelled: false);

        // Decision B: one count-aware confirm for power (Cancel is the dialog's default).
        if (isPower && !await _confirmAsync(BulkConfirmPrompt(command, eligible.Count)))
            return new BulkResult(command, Sent: 0, Skipped: skipped, Cancelled: true);

        int done = 0;
        foreach (var t in eligible)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                switch (command)
                {
                    case StudentCommand.Lock: await _sink.LockAsync(t.EndpointId, locked: true, reliable: true, ct); break;
                    case StudentCommand.Unlock: await _sink.LockAsync(t.EndpointId, locked: false, reliable: true, ct); break;
                    default: await _sink.PowerAsync(t.EndpointId, ToMessageType(command), reliable: true, ct); break;
                }
                done++;
                progress?.Report((done, eligible.Count));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Bulk {command} → {t.EndpointId} failed: {ex.Message}");
            }
        }
        return new BulkResult(command, Sent: done, Skipped: skipped, Cancelled: false);
    }

    /// <summary>The count-aware batch confirm body for a bulk power action.</summary>
    public static string BulkConfirmPrompt(StudentCommand command, int count)
    {
        string verb = command switch
        {
            StudentCommand.Logoff => "Log off",
            StudentCommand.Restart => "Restart",
            StudentCommand.Shutdown => "Shut down",
            _ => command.ToString(),
        };
        string who = count == 1 ? "1 student" : $"{count} students";
        return $"{verb} {who}? Unsaved work on those computers will be lost.";
    }

    /// <summary>Maps a power <see cref="StudentCommand"/> to its wire <see cref="MessageType"/>.
    /// Throws for non-power commands (a programming error).</summary>
    public static MessageType ToMessageType(StudentCommand command) => command switch
    {
        StudentCommand.Logoff => MessageType.ForceLogoff,
        StudentCommand.Restart => MessageType.ForceRestart,
        StudentCommand.Shutdown => MessageType.ForceShutdown,
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Not a power command"),
    };

    /// <summary>The confirm-dialog body for a power action (matches the shipped Yes/No intent;
    /// irreversible actions get the sharper wording).</summary>
    public static string ConfirmPrompt(StudentCommand command, string studentName) => command switch
    {
        StudentCommand.Logoff => $"Log off “{studentName}”? This ends their session and closes their apps.",
        StudentCommand.Restart => $"Restart “{studentName}”? Unsaved work on that computer will be lost.",
        StudentCommand.Shutdown => $"Shut down “{studentName}”? Unsaved work on that computer will be lost.",
        _ => $"Send {command} to “{studentName}”?",
    };
}
