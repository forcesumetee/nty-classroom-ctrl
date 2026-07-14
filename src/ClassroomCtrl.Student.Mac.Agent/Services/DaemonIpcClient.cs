using System;
using System.Buffers.Binary;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Student.Mac.Agent.Services;

/// <summary>
/// Thin, UI-agnostic IPC client that connects the tray Agent to the headless daemon
/// (ClassroomCtrl.Student.Mac) over its Unix domain socket (<see cref="IpcSocket.StudentAgentPath"/>).
///
/// It reads the same length-prefixed frames the daemon writes — <c>[4-byte big-endian Int32 length]
/// + MessagePack(Envelope)</c> — and raises plain events. It NEVER touches Avalonia: the App layer
/// marshals every event onto the UI thread via <c>Dispatcher.UIThread</c> before mutating UI state.
///
/// Resilience: a single background loop owns connect → read → disconnect. If the daemon isn't running
/// yet, dies, or restarts, the loop waits (capped exponential backoff) and reconnects — so the order the
/// two processes start in, and daemon restarts, are both non-events for the user. <see cref="Connected"/>
/// / <see cref="Disconnected"/> fire only on real transitions, which drives the tray "Status" line.
/// </summary>
public sealed class DaemonIpcClient : IDisposable
{
    /// <summary>Must match the daemon's <c>MaxFrameSize</c> (16 MB) — a larger prefix means desync; drop + reconnect.</summary>
    private const int MaxFrameSize = 16 * 1024 * 1024;
    private const int MinBackoffMs = 500;
    private const int MaxBackoffMs = 5000;

    private readonly string _socketPath;
    private readonly object _gate = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private NetworkStream? _stream;          // live write stream (set while connected)
    private readonly object _sendLock = new();
    private readonly SemaphoreSlim _writeSem = new(1, 1);   // serialize concurrent Agent→daemon writes
    private bool _connected;
    private bool _disposed;

    /// <summary>Raised (on a background thread) when the IPC link to the daemon is established.</summary>
    public event Action? Connected;
    /// <summary>Raised (on a background thread) when the IPC link drops (daemon stopped / restarting).</summary>
    public event Action? Disconnected;
    /// <summary>Raised (on a background thread) for each envelope the daemon pushes to the Agent.</summary>
    public event Action<Envelope>? EnvelopeReceived;

    /// <summary>Whether the IPC link to the daemon is currently up.</summary>
    public bool IsConnected { get { lock (_gate) return _connected; } }

    public DaemonIpcClient(string? socketPath = null)
        => _socketPath = socketPath ?? IpcSocket.StudentAgentPath;

    /// <summary>Start the connect/reconnect loop. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _loop is not null) return;
            _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunAsync(_cts.Token));
        }
    }

    /// <summary>Stop the loop and drop any live connection. Safe to call more than once.</summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }
        cts?.Cancel();
        if (loop is not null)
        {
            try { await loop.ConfigureAwait(false); } catch { /* loop swallows its own errors */ }
        }
        cts?.Dispose();
    }

    // ──────────────────────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        int backoff = MinBackoffMs;
        while (!ct.IsCancellationRequested)
        {
            Socket? sock = null;
            try
            {
                // The daemon removes a stale socket file at startup and re-binds; if it isn't up yet
                // the file may be absent — treat that like a failed connect and back off.
                if (File.Exists(_socketPath))
                {
                    sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await sock.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct).ConfigureAwait(false);

                    backoff = MinBackoffMs;         // healthy connection resets the backoff
                    var stream = new NetworkStream(sock, ownsSocket: false);
                    lock (_sendLock) _stream = stream;
                    SetConnected(true);
                    try { await ReadLoopAsync(stream, ct).ConfigureAwait(false); }   // returns on EOF / read error
                    finally { lock (_sendLock) _stream = null; }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception) { /* connect refused / reset / bad frame — fall through to backoff */ }
            finally
            {
                SetConnected(false);
                try { sock?.Dispose(); } catch { /* ignore */ }
            }

            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = Math.Min(backoff * 2, MaxBackoffMs);
        }
        SetConnected(false);
    }

    /// <summary>
    /// Send a framed envelope to the daemon (e.g. a chat reply). Best-effort: silently no-ops when not
    /// connected — the caller (chat send) already reflects the message locally, and the daemon is the
    /// authority that actually relays it to the Teacher. Thread-safe against nothing else writing.
    /// </summary>
    public async Task SendAsync(MessageType type, byte[] payload, CancellationToken ct = default)
    {
        NetworkStream? stream;
        lock (_sendLock) stream = _stream;
        if (stream is null) return;

        var env = Envelope.Create(type, payload, Guid.Empty);   // identity is stamped daemon-side
        var body = env.Serialize();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);

        await _writeSem.WaitAsync(ct).ConfigureAwait(false);
        try { await stream.WriteAsync(frame, ct).ConfigureAwait(false); }
        catch { /* link dropped mid-send; the read loop will reconnect */ }
        finally { _writeSem.Release(); }
    }

    private async Task ReadLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var lengthBuf = new byte[4];

        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactAsync(stream, lengthBuf, ct).ConfigureAwait(false))
                return;   // clean EOF — daemon closed the connection

            int frameLen = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
            if (frameLen <= 0 || frameLen > MaxFrameSize)
                return;   // framing desync — drop and let the outer loop reconnect

            var payload = new byte[frameLen];
            if (!await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
                return;   // mid-frame EOF

            Envelope env;
            try { env = Envelope.Deserialize(payload); }
            catch { continue; }   // skip a single malformed frame; keep the stream

            try { EnvelopeReceived?.Invoke(env); }
            catch { /* a subscriber throwing must not kill the read loop */ }
        }
    }

    private void SetConnected(bool value)
    {
        bool changed;
        lock (_gate)
        {
            changed = _connected != value;
            _connected = value;
        }
        if (!changed) return;
        try { (value ? Connected : Disconnected)?.Invoke(); }
        catch { /* subscriber errors are their own problem */ }
    }

    /// <summary>Read exactly <paramref name="buffer"/>.Length bytes; false on EOF (connection closed).</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct).ConfigureAwait(false);
            if (n == 0) return false;
            total += n;
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        try { StopAsync().GetAwaiter().GetResult(); } catch { /* best effort */ }
    }
}
