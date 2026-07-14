using System;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-7-B — the chat / hand-raise / reaction seam. Re-exposes the already-ported
/// <c>ControlServer</c> inbound events + outbound sends so the grid + chat VMs depend
/// on an interface (fakeable in the TT-7-E aggregation gate), not the concrete server.
/// Mirrors <see cref="IStudentStreamSource"/> / <c>IStudentCommandSink</c>.
///
/// Attribution contract: on every inbound event the student's identity is the
/// <c>EndpointId</c> — <c>ControlServer.OnMessage</c> overrides the payload's id from
/// <c>Envelope.SenderId</c>, which equals the roster/tile <c>EndpointId</c>. The grid
/// maps each event onto the tile with the matching id — never a broadcast-to-all-tiles.
/// </summary>
public interface ITeacherMessaging
{
    // ── Inbound: student → teacher ──
    event EventHandler<ChatMessage>? ChatReceived;
    event EventHandler<HandRaiseMessage>? HandRaiseReceived;
    event EventHandler<(Guid SenderId, ReactionMessage Msg)>? ReactionReceived;

    // ── Outbound: teacher → students ──
    Task BroadcastChatAsync(string text, CancellationToken ct);

    /// <summary>Direct message to one student. Routes <b>reliable:true</b> (baked in) —
    /// a DM is not an ephemeral frame; losing it silently is the TT-5-A lossy-channel
    /// class. Privacy is receiver-side: <c>ChatDirect</c> is a targeted envelope, dropped
    /// by every non-recipient's <c>StudentEnvelopeFilter.IsForMe</c> (TT-6-D).</summary>
    Task SendDirectMessageAsync(Guid endpointId, string text, CancellationToken ct);

    Task BroadcastReactionAsync(ReactionMessage msg, CancellationToken ct);

    /// <summary>Teacher "Recognize" — lower one student's raised hand (targeted HandLower).</summary>
    Task SendHandLowerAsync(Guid studentId, CancellationToken ct);
}
