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
    IReadOnlyList<Guid> ConnectedPeers { get; }
}
