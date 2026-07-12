using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

public enum WireStatus { Disconnected, Connecting, Connected, Reconnecting }

public enum WireDirection { Tx, Rx, System }

/// <summary>
/// Phase 26.0 — the macOS Sandbox's student-side wire transport, promoted from the
/// Phase 24.2 CrossPlatformInteropTest (byte-verified against the shipped Windows
/// Teacher). Reproduces the shipped Student's transport behavior exactly (verified
/// against Student.Service/ClassroomWorker.cs):
///
///   • Framing = [4-byte big-endian Int32 length] + MessagePack(Envelope) body.
///   • On connect (5 s timeout) → send Hello (makes the student appear as a tile).
///   • Heartbeat = empty-payload Ping every 5 s (keeps the tile alive); the
///     Teacher's Pong replies are silent/no-op.
///   • Persistent read loop → raise <see cref="EnvelopeReceived"/> per inbound frame.
///   • Reconnect backoff 2 s → ×2 → 30 s cap.
///
/// This service is deliberately UI-agnostic (no Avalonia refs) so it can be driven
/// by the ConnectionViewModel *and* by headless tests against tools/MockTeacher.
/// Events fire on background threads — subscribers must marshal to their UI thread.
/// </summary>
public sealed class WireClient
{
    private const int ConnectTimeoutMs = 5000;
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    private const int MaxFrameBytes = 64 * 1024 * 1024;

    /// <summary>Stable identity for this student for the lifetime of the client.</summary>
    public Guid EndpointId { get; } = Guid.NewGuid();

    public WireStatus Status { get; private set; } = WireStatus.Disconnected;

    /// <summary>Raised on every status transition (background thread).</summary>
    public event Action<WireStatus>? StatusChanged;

    /// <summary>Traffic log hook: (direction, label, sizeBytes). Label is the
    /// MessageType name for TX/RX frames, or a human message for System.</summary>
    public event Action<WireDirection, string, int>? Traffic;

    /// <summary>Raised per decoded inbound envelope (for dispatch/reflection, 26.0-C).</summary>
    public event Action<Envelope>? EnvelopeReceived;

    private NetworkStream? _stream;
    private readonly object _sendLock = new();

    /// <summary>
    /// Connect + pump with automatic reconnect until <paramref name="ct"/> cancels.
    /// Returns only when cancelled (i.e. Disconnect requested).
    /// </summary>
    public async Task RunAsync(string ip, int port, string displayName, CancellationToken ct)
    {
        int backoffMs = 2000;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SetStatus(WireStatus.Connecting);
                    Log(WireDirection.System, $"Connecting to {ip}:{port} …");
                    await ConnectAndPumpAsync(ip, port, displayName, ct);
                    // Returns cleanly when the Teacher closes the connection.
                    Log(WireDirection.System, "Connection closed by Teacher.");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    Log(WireDirection.System, $"Connection error: {ex.Message}");
                }

                if (ct.IsCancellationRequested) break;

                SetStatus(WireStatus.Reconnecting);
                Log(WireDirection.System, $"Reconnecting in {backoffMs / 1000}s …");
                try { await Task.Delay(backoffMs, ct); }
                catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(backoffMs * 2, 30000);
            }
        }
        finally
        {
            SetStatus(WireStatus.Disconnected);
            Log(WireDirection.System, "Disconnected.");
        }
    }

    private async Task ConnectAndPumpAsync(string ip, int port, string displayName, CancellationToken ct)
    {
        using var client = new TcpClient();

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectCts.CancelAfter(ConnectTimeoutMs);
            try { await client.ConnectAsync(IPAddress.Parse(ip), port, connectCts.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException($"connect timed out after {ConnectTimeoutMs / 1000}s"); }
        }

        _stream = client.GetStream();
        SetStatus(WireStatus.Connected);
        Log(WireDirection.System, $"TCP connected to {ip}:{port} (endpoint {Short(EndpointId)}).");

        // 1) Hello — exactly the shape the shipped Student sends → appear as a tile.
        var hello = new HelloMessage
        {
            MachineName = Environment.MachineName,
            DisplayName = displayName,
            OsVersion = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ProtocolVersion = NetworkConstants.ProtocolVersion,
            EndpointId = EndpointId,
        };
        await SendAsync(MessageType.Hello, MessagePackSerializer.Serialize(hello), ct);

        // 2) Heartbeat loop alongside the read loop (both die when the link drops).
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Task.Run(() => HeartbeatLoopAsync(linkCts.Token), linkCts.Token);

        try
        {
            await ReadLoopAsync(_stream, ct);
        }
        finally
        {
            linkCts.Cancel();
            try { await heartbeat; } catch { /* expected on cancel */ }
            _stream = null;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(HeartbeatInterval, ct); }
            catch (OperationCanceledException) { return; }
            try { await SendAsync(MessageType.Ping, Array.Empty<byte>(), ct); }
            catch { /* the read loop will notice the drop and trigger reconnect */ }
        }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var lengthBuf = new byte[4];
        while (!ct.IsCancellationRequested)
        {
            if (await ReadExactAsync(stream, lengthBuf, 4, ct) == 0)
                return; // Teacher closed the connection.
            int len = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
            if (len <= 0 || len > MaxFrameBytes)
                throw new InvalidOperationException($"bad frame size {len}");

            var body = new byte[len];
            await ReadExactAsync(stream, body, len, ct);

            Envelope env;
            try { env = Envelope.Deserialize(body); }
            catch (Exception ex) { Log(WireDirection.System, $"Undecodable frame ({len} B): {ex.Message}"); continue; }

            Log(WireDirection.Rx, env.Type.ToString(), len + 4);
            EnvelopeReceived?.Invoke(env);
        }
    }

    /// <summary>Send a framed envelope. Thread-safe against the heartbeat loop.</summary>
    public async Task SendAsync(MessageType type, byte[] payload, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("not connected");
        var env = Envelope.Create(type, payload, EndpointId);
        var body = env.Serialize();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);

        // One WriteAsync per frame + a lock so heartbeat Pings never interleave
        // with a larger send mid-frame.
        Task write;
        lock (_sendLock) { write = stream.WriteAsync(frame, ct).AsTask(); }
        await write;
        Log(WireDirection.Tx, type.ToString(), frame.Length);
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

    private void SetStatus(WireStatus s)
    {
        if (Status == s) return;
        Status = s;
        StatusChanged?.Invoke(s);
    }

    private void Log(WireDirection dir, string label, int size = 0) => Traffic?.Invoke(dir, label, size);

    private static string Short(Guid g) => g.ToString()[..8];
}
