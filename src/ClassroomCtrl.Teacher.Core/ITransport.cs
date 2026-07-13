using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Networking;

public interface ITransport : IDisposable
{
    event EventHandler<Envelope>? MessageReceived;
    event EventHandler<Guid>? PeerConnected;
    event EventHandler<Guid>? PeerDisconnected;

    Task StartAsync(CancellationToken ct);
    Task SendAsync(Guid peerId, Envelope envelope, CancellationToken ct);
    Task BroadcastAsync(Envelope envelope, CancellationToken ct);

    /// <summary>
    /// Phase 10.21 — back-pressured send for messages that MUST NOT be dropped.
    /// <see cref="BroadcastAsync"/> enqueues into a small, DropOldest-bounded
    /// per-peer channel (designed for screen-share frames where the newer
    /// frame supersedes the older).  File chunks share no such substitution
    /// property: dropping any chunk corrupts the file.  This overload routes
    /// to a separate per-peer channel whose FullMode is Wait, so the producer
    /// is back-pressured by the slowest peer's drain rate rather than silently
    /// losing chunks.  Use for FileAnnounce/FileChunk/FileComplete (and any
    /// future "must-arrive" traffic).
    /// </summary>
    Task BroadcastReliableAsync(Envelope envelope, CancellationToken ct);

    IReadOnlyList<Guid> ConnectedPeers { get; }
}
