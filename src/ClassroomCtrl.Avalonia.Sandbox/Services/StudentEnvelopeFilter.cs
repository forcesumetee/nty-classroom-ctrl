using System;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// TT-6-D fix — the Student's receive-side target filter. The shipped Windows Student runs
/// this in its <c>Service</c> (<c>ClassroomWorker.IsForMe</c>), dropping non-matching targeted
/// envelopes before the Agent acts. The macOS port collapsed Service+Agent into one process
/// (the Sandbox) and DROPPED this filter, so every targeted command (lock/unlock/policy/power/
/// DM/screen-stream/mic) — broadcast to all peers by the Teacher and meant to be filtered
/// client-side — was acted on by EVERY Mac student, not just the target. Verified: the shipped
/// Windows Student HAS this filter (ClassroomWorker.IsForMe) → customer A was never exposed;
/// this was a port omission, fixed here.
///
/// 🔴 DEFAULT-DENY (like <see cref="StudentPlatform.CanReceivePower"/>): match a KNOWN pass
/// condition → pass; EVERYTHING ELSE → drop. A future command type or targeting shape that
/// forgets to set <c>TargetEndpointId</c> must fail CLOSED (dropped), never fail open (blast to
/// everyone). Do not rewrite this as "drop known-bad, pass the rest."
/// </summary>
public static class StudentEnvelopeFilter
{
    /// <summary>
    /// True iff this envelope should be acted on by the student with <paramref name="myEndpointId"/>
    /// (and optionally in breakout room <paramref name="myRoomId"/>, inert until breakout ports).
    /// PASS conditions (all else DROPS):
    /// <list type="bullet">
    ///   <item>targeted at me: <c>TargetEndpointId == myEndpointId</c></item>
    ///   <item>targeted at my group: <c>TargetGroupId == myRoomId</c> (both set)</item>
    ///   <item>whole-class broadcast: <c>TargetEndpointId == Empty</c> AND no group target</item>
    /// </list>
    /// </summary>
    public static bool IsForMe(Envelope env, Guid myEndpointId, Guid? myRoomId = null)
    {
        // An explicit endpoint target always wins (a message to ONE student in a group is still
        // for that student). Order mirrors the shipped ClassroomWorker.IsForMe.
        if (env.TargetEndpointId == myEndpointId) return true;
        if (env.TargetGroupId.HasValue && myRoomId.HasValue
            && env.TargetGroupId.Value == myRoomId.Value) return true;
        if (env.TargetEndpointId == Guid.Empty && !env.TargetGroupId.HasValue) return true;
        return false;   // default-deny: unanticipated shapes fail closed
    }
}
