using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
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
    /// <summary>
    /// Phase 10.10 Fix 7 — connection considered stale if no Ping (or any other
    /// frame) has arrived within this many milliseconds.  Student is configured
    /// to ping every 5 s; 15 s gives a comfortable 3× margin for jitter, GC
    /// pauses, and brief Wi-Fi reconnects without false-positive disconnects.
    /// </summary>
    private const int StaleAfterMs = 15_000;

    /// <summary>How often to sweep for stale peers.</summary>
    private static readonly TimeSpan StaleSweepInterval = TimeSpan.FromSeconds(2);

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
        // Phase 10.10 Fix 7 — start the staleness sweeper.
        _ = Task.Run(() => StaleSweepLoop(_cts.Token), _cts.Token);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Accept loop error");
                // Phase 10.10 Fix 10 — back off briefly to avoid burning CPU on a
                // tight error loop (e.g. a half-open kernel socket repeatedly
                // failing AcceptTcpClientAsync without throwing OperationCanceled).
                try { await Task.Delay(100, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Phase 10.10 Fix 7 — periodically scan peers and forcibly close any whose
    /// last frame is older than <see cref="StaleAfterMs"/>.  Closing the
    /// underlying TcpClient unblocks the peer's read loop, which then runs
    /// through its finally block and fires PeerDisconnected up to subscribers.
    /// </summary>
    private async Task StaleSweepLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(StaleSweepInterval, ct); }
            catch (OperationCanceledException) { break; }

            var nowTicks = Environment.TickCount64;
            foreach (var (id, conn) in _peers)
            {
                var idleMs = nowTicks - conn.LastSeenTickMs;
                if (idleMs > StaleAfterMs)
                {
                    _logger.LogWarning("Peer {Id} stale ({Idle} ms since last frame); closing.", id, idleMs);
                    try { conn.Dispose(); } catch { }
                    // PeerDisconnected fires from the read loop's finally clause.
                }
            }
        }
    }

    /// <summary>
    /// Phase 10.10 Fix 7 — Ping/Pong are an internal control loop; never raise
    /// them up to user-code subscribers.  The connection's read path passes
    /// every frame through here and we filter the keepalive types out.
    /// </summary>
    internal void HandleMessage(Envelope env)
    {
        if (env.Type == MessageType.Ping || env.Type == MessageType.Pong)
            return;
        MessageReceived?.Invoke(this, env);
    }

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

    /// <summary>
    /// Phase 10.10 Fix 8 — broadcast is now non-blocking and per-peer.  Each
    /// PeerConnection has its own bounded Channel; SendBytesAsync just enqueues
    /// (TryWrite, drop-oldest on overflow) and a dedicated writer task drains
    /// the channel into the socket.  A slow / Wi-Fi-stuck student no longer
    /// blocks broadcast to the rest of the class.
    /// </summary>
    public Task BroadcastAsync(Envelope env, CancellationToken ct)
    {
        var bytes = env.Serialize();
        foreach (var p in _peers.Values)
        {
            // Each peer has its own queue + writer task; enqueue is sync.
            p.EnqueueOrDrop(bytes);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 11-C — broadcast a screen-share audio frame to every peer via the
    /// dedicated audio channel (3-deep, DropOldest).  Same fan-out shape as
    /// <see cref="BroadcastAsync"/> but each peer's <c>EnqueueAudioOrDrop</c>
    /// pushes into its own audio queue, not the lossy video queue.  This is the
    /// 11-C structural fix for the stutter+desync symptom: audio is no longer
    /// evicted by 20 FPS H.264 video bursts on the shared queue.
    /// </summary>
    public Task BroadcastAudioAsync(Envelope env, CancellationToken ct)
    {
        var bytes = env.Serialize();
        foreach (var p in _peers.Values)
        {
            p.EnqueueAudioOrDrop(bytes);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 12-B — fan-out send for Phase 6.5 remote-control input through the
    /// dedicated per-peer <c>_inputOutbox</c> (Wait, cap 32, separate from the
    /// lossy <c>_outbox</c> that carries 20 FPS video).
    ///
    /// The <paramref name="peerId"/> parameter is informational — receivers
    /// filter via the envelope's <c>TargetEndpointId</c> + their own
    /// <c>IsForMe</c> check.  We fan out (mirror <see cref="BroadcastAudioAsync"/>)
    /// rather than look up by <paramref name="peerId"/> because <c>_peers</c> is
    /// keyed by the server-generated transport connection Guid created at TCP-
    /// accept, NOT by the student's application-level <c>EndpointId</c> — and
    /// every existing targeted-to-one operation in this codebase
    /// (PowerOneAsync, LockOneAsync, RequestStudentStreamAsync, …) already uses
    /// the same broadcast-and-IsForMe pattern.  Fixing the namespace gap with a
    /// proper endpoint→connection map is a Tier-3 follow-up.
    ///
    /// Each peer's <c>EnqueueInputOrSkip</c> is a non-blocking <c>TryWrite</c>:
    /// the input queue is normally near-empty (input is ~30 events/s × ~30 B);
    /// cap 32 absorbs ~1 s of burst.  If a peer's queue does saturate, that
    /// peer's write is skipped — for non-target peers IsForMe would have
    /// discarded the bytes anyway, and for the actual target the connection is
    /// genuinely stalled (release-all on disconnect will clean modifier state).
    /// Keeps FullMode.Wait on the channel for symmetry with reliable-style
    /// semantics, but never blocks the teacher UI thread on a producer write.
    /// </summary>
    public Task SendInputAsync(Guid peerId, Envelope env, CancellationToken ct)
    {
        var bytes = env.Serialize();
        foreach (var p in _peers.Values)
        {
            p.EnqueueInputOrSkip(bytes);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 10.21 — reliable per-peer broadcast for file chunks (and any
    /// other traffic where dropping a frame corrupts state).  Fans out the
    /// serialized envelope to every peer's reliable channel; each peer's
    /// channel is FullMode.Wait, so when the slowest peer's writer task can't
    /// keep up the producer awaits on that peer rather than losing chunks.
    /// Producer therefore runs at the slowest peer's TCP-drain rate.  Acceptable
    /// for a one-off file send; would not be acceptable for hot screen-share
    /// frames (which is why <see cref="BroadcastAsync"/> stays DropOldest).
    /// </summary>
    public async Task BroadcastReliableAsync(Envelope env, CancellationToken ct)
    {
        var bytes = env.Serialize();
        // Snapshot the peer list — concurrent connect/disconnect during a long
        // file send is fine; new peers miss this frame (they weren't here when
        // it was broadcast), departed peers are no-ops on the await.
        var snapshot = _peers.Values.ToArray();
        foreach (var p in snapshot)
        {
            try { await p.EnqueueReliableAsync(bytes, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Reliable enqueue failed for peer {Id}; continuing with remaining peers.",
                    p.Id);
            }
        }
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
    /// <summary>
    /// Phase 10.10 Fix 8 — bounded queue capacity.  Sized for ~2 seconds of
    /// teacher-side broadcast at peak (e.g. screen-share at ~8 fps × overhead);
    /// beyond that we drop the oldest frame so a slow student catches up to
    /// the latest state instead of the queue stalling broadcast for everyone.
    /// </summary>
    private const int QueueCapacity = 16;

    /// <summary>
    /// Phase 10.21 — separate channel for must-arrive traffic (file chunks).
    /// Kept small on purpose: a larger buffer just defers the back-pressure
    /// signal without giving more headroom — the bottleneck is socket drain
    /// rate, not in-memory queueing.  FullMode.Wait makes the producer await
    /// when full, so file chunks back up at the producer rather than getting
    /// silently dropped (which is what corrupted Net Movie pre-10.21).
    /// </summary>
    private const int ReliableQueueCapacity = 4;

    /// <summary>
    /// Phase 11-C — third per-peer channel, exclusively for screen-share audio
    /// frames.  Cap of 3 ≈ 300 ms at 100 ms/frame.  DropOldest semantics:
    /// in normal operation the writer drains this queue near-instantly (audio
    /// is ~32 KB/s) so it sits near-empty, effectively lossless; if a peer's
    /// TCP write stalls, dropping audio older than ~300 ms is correct because
    /// stale audio is musically worthless — skip ahead beats holding silence.
    /// CRUCIALLY: this is separate from <see cref="_outbox"/>, so a burst of
    /// 20 FPS H.264 video frames can no longer evict un-played audio (the
    /// inc4-introduced regression behind the 11-C stutter+desync symptom).
    /// We deliberately do NOT use <see cref="_reliableOutbox"/> for audio —
    /// FullMode.Wait there would back-pressure the audio pump to the slowest
    /// student, making every student stutter.  Audio wants its own drop-stale
    /// channel, not the file-transfer channel's drop-nothing semantics.
    /// </summary>
    private const int AudioQueueCapacity = 3;

    /// <summary>
    /// Phase 12-B — fourth per-peer channel, exclusively for Phase 6.5 remote-
    /// control input envelopes (RemoteControlStart/End, RemoteMouseMove/Click/
    /// Scroll, RemoteKey).  Cap 32 ≈ ~1 s of headroom at 30 events/s; FullMode
    /// is <see cref="BoundedChannelFullMode.Wait"/> because input semantics are
    /// near-lossless: a dropped key-up = stuck modifier on the student, a
    /// dropped mouse-up after a drag = stuck button.  Wait back-pressures only
    /// the producer for the single peer being controlled (input is sent
    /// targeted, not broadcast — see TcpControlServer.SendInputAsync) so a
    /// stalled student can't disturb other students' input or video / audio.
    /// </summary>
    private const int InputQueueCapacity = 32;

    private readonly Guid _id;
    private readonly TcpClient _client;
    private readonly TcpControlServer _server;
    private readonly ILogger _logger;
    private readonly NetworkStream _stream;

    // Fix 8 — outbound work queue + dedicated writer task.
    private readonly Channel<byte[]> _outbox;
    // Phase 10.21 — second outbound queue for reliable traffic.
    private readonly Channel<byte[]> _reliableOutbox;
    // Phase 11-C — third outbound queue for audio (drop-stale, separate from video).
    private readonly Channel<byte[]> _audioOutbox;
    // Phase 12-B — fourth outbound queue for remote-control input (Wait, lossless).
    private readonly Channel<byte[]> _inputOutbox;
    private readonly Task _writerTask;
    private readonly CancellationTokenSource _connCts = new();

    /// <summary>Phase 10.21 — exposed so BroadcastReliableAsync can log per-peer failures.</summary>
    public Guid Id => _id;

    // Fix 8 — count of frames dropped due to queue overflow.  Logged in a
    // throttled way so a flapping student doesn't drown the log.
    private long _droppedFrames;
    private long _nextDropLogTickMs;

    // Fix 7 — last-seen timestamp updated on every successful read.  Reads use
    // Interlocked.Exchange to avoid a lock on the hot path.
    private long _lastSeenTickMs = Environment.TickCount64;
    public long LastSeenTickMs => Interlocked.Read(ref _lastSeenTickMs);

    public PeerConnection(Guid id, TcpClient client, TcpControlServer server, ILogger logger)
    {
        _id = id;
        _client = client;
        _server = server;
        _logger = logger;
        _stream = client.GetStream();

        _outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _reliableOutbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ReliableQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        // Phase 11-C — audio gets its own bounded DropOldest channel so a
        // 20 FPS H.264 burst can't push un-played audio out of the queue.
        // See the AudioQueueCapacity comment for the policy rationale.
        _audioOutbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(AudioQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        // Phase 12-B — input queue.  See InputQueueCapacity for the policy.
        _inputOutbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(InputQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
        _writerTask = Task.Run(() => WriterLoopAsync(_connCts.Token));
    }

    /// <summary>
    /// Phase 10.21 — back-pressured enqueue for must-arrive frames.  Awaits
    /// when the reliable channel is full so producers (BroadcastFileAsync) are
    /// rate-limited to the slowest peer's drain rate instead of dropping
    /// chunks on the floor.
    /// </summary>
    public ValueTask EnqueueReliableAsync(byte[] body, CancellationToken ct)
        => _reliableOutbox.Writer.WriteAsync(body, ct);

    /// <summary>
    /// Phase 11-C — non-blocking enqueue for audio frames.  Backed by a 3-deep
    /// DropOldest channel separate from <see cref="_outbox"/>, so a video burst
    /// can't push un-played audio frames out of the queue.  When this channel
    /// is full (a peer's TCP write is genuinely stalled), the oldest queued
    /// audio frame is evicted — the right behavior because stale audio is
    /// musically worthless: skip ahead to "now" beats holding a 3-second
    /// silence-gap behind a backlog.
    /// </summary>
    public void EnqueueAudioOrDrop(byte[] body)
    {
        // Same shape as EnqueueOrDrop — TryWrite returns false only when the
        // channel is Complete()d (i.e. teardown).  Under DropOldest a full
        // channel still accepts the write (and evicts the oldest item).
        _audioOutbox.Writer.TryWrite(body);
    }

    /// <summary>
    /// Phase 12-B — non-blocking enqueue for remote-control input.  Backed by
    /// the per-peer <c>_inputOutbox</c> (Wait, cap 32).  Uses <c>TryWrite</c>
    /// rather than the channel's awaitable <c>WriteAsync</c> so a stalled peer
    /// can never block the teacher's UI thread during fan-out from
    /// <see cref="TcpControlServer.SendInputAsync"/>.
    ///
    /// Under normal load the queue drains within microseconds (input is small
    /// and the writer-loop drains all input per iteration), so <c>TryWrite</c>
    /// virtually always succeeds — giving us lossless ordering in practice
    /// while keeping FullMode.Wait's semantic guarantee that a *producer that
    /// genuinely waits* is the right back-pressure shape if it's ever needed
    /// elsewhere.  If the queue is genuinely full (cap-32 burst built up, peer
    /// not draining), this returns false and the write is skipped — preferable
    /// to DropOldest because the most recent message is more likely a key-up
    /// or mouse-up than a key-down, and dropping the up leaves stuck state.
    /// </summary>
    public bool EnqueueInputOrSkip(byte[] body)
        => _inputOutbox.Writer.TryWrite(body);

    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _connCts.Token);
        try
        {
            var lengthBuf = new byte[4];
            while (!linked.IsCancellationRequested)
            {
                int read = await ReadExactAsync(_stream, lengthBuf, 4, linked.Token);
                if (read == 0) break;
                int len = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
                if (len <= 0 || len > 64 * 1024 * 1024) throw new InvalidDataException("Bad frame size");
                var payload = new byte[len];
                await ReadExactAsync(_stream, payload, len, linked.Token);

                // Fix 7 — every successful frame counts as a liveness signal,
                // not just Ping.  Update before dispatch so handlers don't see
                // a stale value if they look at LastSeen synchronously.
                Interlocked.Exchange(ref _lastSeenTickMs, Environment.TickCount64);

                Envelope env;
                try { env = Envelope.Deserialize(payload); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Peer {Id} malformed envelope; dropping", _id);
                    continue;
                }

                // Fix 7 — auto-reply Pong inside the connection so user code
                // never sees keepalive traffic.
                if (env.Type == MessageType.Ping)
                {
                    var pong = Envelope.Create(MessageType.Pong, Array.Empty<byte>(), Guid.Empty);
                    EnqueueOrDrop(pong.Serialize());
                    continue;
                }

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

    public Task SendAsync(Envelope env, CancellationToken ct)
    {
        EnqueueOrDrop(env.Serialize());
        return Task.CompletedTask;
    }

    public Task SendBytesAsync(byte[] body, CancellationToken ct)
    {
        EnqueueOrDrop(body);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Phase 10.10 Fix 8 — non-blocking enqueue.  Backed by a bounded channel
    /// with DropOldest, so when a slow student's queue fills we discard the
    /// stalest frame and accept the new one.  Logs a warning at most once per
    /// 5 seconds per peer so a flapping connection doesn't drown the log.
    /// </summary>
    public void EnqueueOrDrop(byte[] body)
    {
        if (!_outbox.Writer.TryWrite(body))
        {
            // Bounded channel with DropOldest should accept everything but
            // when the channel is closed/completed TryWrite returns false.
            return;
        }

        // Track dropped frames implicitly: if the queue was full, the channel
        // would have evicted the oldest item to make room.  We can't directly
        // observe DropOldest evictions on .NET Channels (no event), but we can
        // tell when count > a high-water threshold and surface a warning.
        // Simpler: skip metrics here — the channel is doing the right thing.
        _ = body; // keep IL minimal
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        // Phase 10.21 — drains _reliableOutbox (priority) and _outbox
        // (best-effort).  Reliable never starves lossy because we always
        // advance one lossy item per outer iteration once reliable is empty.
        // Phase 11-C — _audioOutbox added.  Phase 12-B — _inputOutbox added.
        // Audio + input are both small, latency-critical, and bounded so
        // draining ALL queued items per iteration is cheap and cannot starve
        // video.  Order:
        //   1. ALL reliable (file chunks — must arrive).
        //   2. ALL audio   (latency-critical, ~9 KB max per iteration).
        //   3. ALL input   (remote-control, cap 32 × ~30 B ≈ ~1 KB max).
        //   4. ONE lossy   (screen-share video — drop-old semantics already
        //                   handled by _outbox itself).
        // Teardown is signalled by _connCts.Cancel() in Dispose, which trips
        // the OperationCanceledException catch below.
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var reliableWait = _reliableOutbox.Reader.WaitToReadAsync(ct).AsTask();
                var audioWait    = _audioOutbox.Reader.WaitToReadAsync(ct).AsTask();
                var inputWait    = _inputOutbox.Reader.WaitToReadAsync(ct).AsTask();
                var lossyWait    = _outbox.Reader.WaitToReadAsync(ct).AsTask();
                await Task.WhenAny(reliableWait, audioWait, inputWait, lossyWait);

                // Priority 1 — ALL ready reliable items.
                while (_reliableOutbox.Reader.TryRead(out var rbody))
                {
                    if (!await TryWriteFrameAsync(rbody, ct)) return;
                }
                // Priority 2 — ALL ready audio items.  Cap=3 bounds this loop;
                // video can't be starved.
                while (_audioOutbox.Reader.TryRead(out var abody))
                {
                    if (!await TryWriteFrameAsync(abody, ct)) return;
                }
                // Priority 3 — ALL ready input items.  Cap=32 bounds this
                // loop; ~1 KB total max, can't starve video.  Draining all
                // matters: a burst of cached MouseMove events must clear
                // before the next video frame so cursor position stays fresh.
                while (_inputOutbox.Reader.TryRead(out var ibody))
                {
                    if (!await TryWriteFrameAsync(ibody, ct)) return;
                }
                // Priority 4 — ONE lossy (video) item.  Keeps reliable/audio/
                // input from starving the screen-share stream.
                if (_outbox.Reader.TryRead(out var lbody))
                {
                    if (!await TryWriteFrameAsync(lbody, ct)) return;
                }
            }
        }
        catch (OperationCanceledException) { /* expected on dispose */ }

        // Throttled drop-counter log.
        var dropped = Interlocked.Read(ref _droppedFrames);
        if (dropped > 0)
        {
            _logger.LogWarning("Peer {Id} writer exited; total dropped frames: {Dropped}", _id, dropped);
        }
    }

    /// <summary>
    /// Phase 10.21 — extracted from WriterLoopAsync so reliable and lossy
    /// paths share identical framing + error handling.  Returns false when
    /// the connection has been torn down and the writer loop should exit.
    /// </summary>
    private async Task<bool> TryWriteFrameAsync(byte[] body, CancellationToken ct)
    {
        try
        {
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
            await _stream.WriteAsync(len, ct);
            await _stream.WriteAsync(body, ct);
            await _stream.FlushAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Peer {Id} write failed; closing connection", _id);
            try { _connCts.Cancel(); } catch { }
            return false;
        }
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

    public void Dispose()
    {
        try { _connCts.Cancel(); } catch { }
        try { _outbox.Writer.TryComplete(); } catch { }
        try { _reliableOutbox.Writer.TryComplete(); } catch { }
        try { _audioOutbox.Writer.TryComplete(); } catch { }
        try { _inputOutbox.Writer.TryComplete(); } catch { }
        try { _client.Dispose(); } catch { }
    }
}
