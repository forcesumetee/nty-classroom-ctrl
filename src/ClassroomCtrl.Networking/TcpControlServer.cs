using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace ClassroomCtrl.Networking;

/// <summary>
/// Length-prefixed TCP server. TLS mutual auth wiring is the responsibility
/// of the host process (see Spec §5.3). This skeleton uses plain TCP for clarity;
/// wrap NetworkStream in SslStream + AuthenticateAsServerAsync before production.
/// </summary>
public class TcpControlServer : ITransport
{
    private readonly ILogger<TcpControlServer> _logger;
    private readonly int _port;
    private readonly ConcurrentDictionary<Guid, PeerConnection> _peers = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    public event EventHandler<Envelope>? MessageReceived;
    public event EventHandler<Guid>? PeerConnected;
    public event EventHandler<Guid>? PeerDisconnected;

    public IReadOnlyList<Guid> ConnectedPeers => _peers.Keys.ToList();

    public TcpControlServer(ILogger<TcpControlServer> logger, int port = NetworkConstants.ControlTcpPort)
    {
        _logger = logger;
        _port = port;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        _logger.LogInformation("TCP control server listening on :{Port}", _port);
        _ = Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                var peerId = Guid.NewGuid();
                var conn = new PeerConnection(peerId, client, this, _logger);
                _peers[peerId] = conn;
                _ = Task.Run(() => conn.RunAsync(ct), ct);
                PeerConnected?.Invoke(this, peerId);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogError(ex, "Accept loop error"); }
        }
    }

    internal void HandleMessage(Envelope env) => MessageReceived?.Invoke(this, env);
    internal void HandleDisconnect(Guid peerId)
    {
        _peers.TryRemove(peerId, out _);
        PeerDisconnected?.Invoke(this, peerId);
    }

    public async Task SendAsync(Guid peerId, Envelope env, CancellationToken ct)
    {
        if (_peers.TryGetValue(peerId, out var conn))
            await conn.SendAsync(env, ct);
    }

    public async Task BroadcastAsync(Envelope env, CancellationToken ct)
    {
        var bytes = env.Serialize();
        await Task.WhenAll(_peers.Values.Select(p => p.SendBytesAsync(bytes, ct)));
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener?.Stop();
        foreach (var p in _peers.Values) p.Dispose();
    }
}

internal class PeerConnection : IDisposable
{
    private readonly Guid _id;
    private readonly TcpClient _client;
    private readonly TcpControlServer _server;
    private readonly ILogger _logger;
    private readonly NetworkStream _stream;

    public PeerConnection(Guid id, TcpClient client, TcpControlServer server, ILogger logger)
    {
        _id = id;
        _client = client;
        _server = server;
        _logger = logger;
        _stream = client.GetStream();
    }

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var lengthBuf = new byte[4];
            while (!ct.IsCancellationRequested)
            {
                int read = await ReadExactAsync(_stream, lengthBuf, 4, ct);
                if (read == 0) break;
                int len = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
                if (len <= 0 || len > 64 * 1024 * 1024) throw new InvalidDataException("Bad frame size");
                var payload = new byte[len];
                await ReadExactAsync(_stream, payload, len, ct);
                var env = Envelope.Deserialize(payload);
                _server.HandleMessage(env);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Peer {Id} dropped", _id);
        }
        finally
        {
            _server.HandleDisconnect(_id);
            Dispose();
        }
    }

    public Task SendAsync(Envelope env, CancellationToken ct) => SendBytesAsync(env.Serialize(), ct);

    public async Task SendBytesAsync(byte[] body, CancellationToken ct)
    {
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        await _stream.WriteAsync(len, ct);
        await _stream.WriteAsync(body, ct);
        await _stream.FlushAsync(ct);
    }

    private static async Task<int> ReadExactAsync(NetworkStream s, byte[] buf, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) return read;
            read += n;
        }
        return read;
    }

    public void Dispose() { try { _client.Dispose(); } catch { } }
}
