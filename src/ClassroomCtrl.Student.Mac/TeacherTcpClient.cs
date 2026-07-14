using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;
using Microsoft.Extensions.Logging;

namespace ClassroomCtrl.Student.Mac;

/// <summary>
/// The daemon's TCP artery to the Teacher's <c>ControlServer</c> (port 7777). Reproduces the shipped
/// Windows Student transport EXACTLY — byte-for-byte the same shape the Avalonia Sandbox's proven
/// <c>WireClient</c> uses (verified cross-platform in Phase 24.3) — so a macOS daemon appears to the
/// Teacher as an ordinary student:
///
///   • Framing = <c>[4-byte big-endian Int32 length] + MessagePack(Envelope)</c> (same as the IPC socket).
///   • On connect (5 s timeout) → send <c>Hello</c> so the student appears as a tile.
///   • Heartbeat = empty-payload <c>Ping</c> every 5 s; the Teacher's <c>Pong</c> is a silent no-op.
///   • Persistent read loop → raise <see cref="EnvelopeReceived"/> per inbound frame (the worker routes
///     these into <c>DispatchTeacherCommandAsync</c>).
///   • Reconnect = aggressive exponential backoff, 1 s → ×2 → 30 s cap, forever until cancelled.
///
/// IDENTITY: the <c>Hello.EndpointId</c> and every envelope's <c>SenderId</c> are the daemon's PERSISTENT
/// endpoint id (passed in). This is load-bearing: the Teacher addresses targeted commands to that id, and
/// the worker's <c>IsForMe</c> matches against the same id — a fresh per-run guid would silently drop every
/// targeted Lock / Power / File.
///
/// UI-agnostic: raises plain events on the read-loop / connect-loop thread. The worker wires
/// <see cref="Connected"/> / <see cref="Disconnected"/> to the dead-man switch + Agent status push, and
/// funnels <see cref="EnvelopeReceived"/> through a single-consumer queue so dispatch stays ordered.
/// </summary>
public sealed class TeacherTcpClient
{
    private const int ConnectTimeoutMs = 5000;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private const int MaxFrameBytes = 64 * 1024 * 1024;
    private const int MinBackoffMs = 1000;
    private const int MaxBackoffMs = 30000;

    private readonly ILogger _logger;
    private readonly Guid _endpointId;
    private readonly string _displayName;
    private readonly object _sendLock = new();
    private readonly object _stateLock = new();

    private NetworkStream? _stream;
    private bool _connected;

    /// <summary>Raised (background thread) when the TCP link to the Teacher is established.</summary>
    public event Action? Connected;
    /// <summary>Raised (background thread) when the TCP link drops (triggers the dead-man switch).</summary>
    public event Action? Disconnected;
    /// <summary>Raised (background thread) for every envelope received from the Teacher.</summary>
    public event Action<Envelope>? EnvelopeReceived;

    public bool IsConnected { get { lock (_stateLock) return _connected; } }

    public TeacherTcpClient(ILogger logger, Guid endpointId, string displayName)
    {
        _logger = logger;
        _endpointId = endpointId;
        _displayName = displayName;
    }

    /// <summary>Connect + pump with automatic reconnect until <paramref name="ct"/> cancels.</summary>
    public async Task RunAsync(string ip, int port, CancellationToken ct)
    {
        int backoff = MinBackoffMs;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _logger.LogInformation("[Teacher] Connecting to {Ip}:{Port} …", ip, port);
                await ConnectAndPumpAsync(ip, port, ct).ConfigureAwait(false);
                _logger.LogInformation("[Teacher] Connection closed by Teacher");
                backoff = MinBackoffMs;   // a clean session resets the backoff
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning("[Teacher] Connection error: {Msg}", ex.Message);
            }

            if (ct.IsCancellationRequested) break;

            _logger.LogInformation("[Teacher] Reconnecting in {Sec}s …", backoff / 1000);
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, MaxBackoffMs);
        }
    }

    private async Task ConnectAndPumpAsync(string ip, int port, CancellationToken ct)
    {
        using var client = new TcpClient();

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(ConnectTimeoutMs);
            try { await client.ConnectAsync(IPAddress.Parse(ip), port, connectCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException($"connect timed out after {ConnectTimeoutMs / 1000}s"); }
        }

        _stream = client.GetStream();
        SetConnected(true);
        _logger.LogInformation("[Teacher] TCP connected to {Ip}:{Port} (endpoint {Id})", ip, port, Short(_endpointId));

        // 1) Hello — exactly the shape the shipped Student sends → appear as a tile on the Teacher.
        var hello = new HelloMessage
        {
            MachineName = Environment.MachineName,
            DisplayName = _displayName,
            OsVersion = RuntimeInformation.OSDescription,
            ProtocolVersion = NetworkConstants.ProtocolVersion,
            EndpointId = _endpointId,
        };
        await SendAsync(MessageType.Hello, MessagePackSerializer.Serialize(hello), ct).ConfigureAwait(false);

        // 2) Heartbeat loop alongside the read loop — both die together when the link drops.
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(() => HeartbeatLoopAsync(linkCts.Token), linkCts.Token);

        try
        {
            await ReadLoopAsync(_stream, ct).ConfigureAwait(false);
        }
        finally
        {
            SetConnected(false);
            linkCts.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch { /* expected on cancel */ }
            _stream = null;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            try { await SendAsync(MessageType.Ping, Array.Empty<byte>(), ct).ConfigureAwait(false); }
            catch { /* the read loop will notice the drop and trigger reconnect */ }
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var lengthBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (await ReadExactAsync(stream, lengthBuf, 4, ct).ConfigureAwait(false) == 0)
                return; // Teacher closed the connection (clean EOF)

            int len = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
            if (len <= 0 || len > MaxFrameBytes)
                throw new InvalidOperationException($"bad frame size {len}");

            var body = new byte[len];
            await ReadExactAsync(stream, body, len, ct).ConfigureAwait(false);

            Envelope env;
            try { env = Envelope.Deserialize(body); }
            catch (Exception ex) { _logger.LogWarning("[Teacher] Undecodable frame ({Len} B): {Msg}", len, ex.Message); continue; }

            try { EnvelopeReceived?.Invoke(env); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Teacher] Envelope handler threw for {Type}", env.Type); }
        }
    }

    /// <summary>Send a framed envelope. Thread-safe against the heartbeat loop.</summary>
    public async Task SendAsync(MessageType type, byte[] payload, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        var env = Envelope.Create(type, payload, _endpointId);
        var body = env.Serialize();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);

        // One WriteAsync per frame under a lock so a heartbeat Ping never interleaves mid-frame.
        Task write;
        lock (_sendLock) { write = stream.WriteAsync(frame, ct).AsTask(); }
        await write.ConfigureAwait(false);
    }

    private static async Task<int> ReadExactAsync(NetworkStream s, byte[] buf, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await s.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) return read;   // EOF
            read += n;
        }
        return read;
    }

    private void SetConnected(bool value)
    {
        bool changed;
        lock (_stateLock) { changed = _connected != value; _connected = value; }
        if (!changed) return;
        try { (value ? Connected : Disconnected)?.Invoke(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Teacher] status subscriber threw"); }
    }

    private static string Short(Guid g) => g.ToString()[..8];
}
