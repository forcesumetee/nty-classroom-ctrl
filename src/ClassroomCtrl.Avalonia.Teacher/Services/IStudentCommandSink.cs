using System;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-5-B (macOS port) — the send seam for per-student STATE-CHANGING commands
/// (lock/unlock, power), mirroring TT-3's <c>IStudentStreamSource</c>. It exists so
/// <see cref="StudentCommandController"/> is unit-testable against a fake sink that
/// records the <paramref name="reliable"/> channel each command was routed on.
///
/// THE SEND-PATH RULE (the reason <paramref name="reliable"/> is on every method and
/// NOT defaulted): state-changing commands MUST go on the reliable channel
/// (<c>ControlServer.SendTargetedAsync(..., reliable:true)</c> → the never-drop
/// <c>_reliableOutbox</c>). The shipped Windows v1.2.1 fix set reliable:true on the
/// BULK path but left the per-student context-menu commands defaulting to
/// reliable:false → the lossy DropOldest video queue, where a command issued while
/// the teacher is screen-sharing can be silently evicted. This port fixes that: the
/// controller passes reliable:true, and the gate ASSERTS it (the channel, not just
/// the send). Making <paramref name="reliable"/> a required parameter with no default
/// means a new command can't silently inherit the lossy default.
/// </summary>
public interface IStudentCommandSink
{
    /// <summary>Lock (or unlock) one student's screen. <paramref name="reliable"/> selects
    /// the transport channel — commands pass true.</summary>
    Task LockAsync(Guid endpointId, bool locked, bool reliable, CancellationToken ct);

    /// <summary>Send a power command to one student. <paramref name="type"/> must be one of
    /// ForceShutdown / ForceRestart / ForceLogoff. <paramref name="reliable"/> selects the
    /// transport channel — commands pass true.</summary>
    Task PowerAsync(Guid endpointId, MessageType type, bool reliable, CancellationToken ct);
}
