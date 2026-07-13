// TT-0 — MockStudent
// ─────────────────────────────────────────────────────────────────────────
// A headless STUDENT stand-in — the inverse of tools/MockTeacher. It reuses the Sandbox's
// student-side WireClient (100%, no fork) to connect to a Mac Teacher server, so Teacher-side
// logic can be proven WITHOUT a Windows box or a second Mac. TT-0-C wires the real
// Screen/Camera/Audio streamers (which emit real native-encoded frames — the ideal input for the
// Teacher's TT-4 H.264 DECODE, as an our-encoder → wire → our-decoder round-trip).
//
// Modes:
//   (default) connect to a Teacher at --ip/--port and run a dispatch loop (log every inbound
//             envelope); Ctrl+C to quit. Used against MockTeacher / the real Mac Teacher.
//   --selftest — spin up a MINIMAL in-process teacher-stub on loopback and drive the real
//             WireClient through connect → Hello → command-receipt → heartbeat Ping/Pong. No
//             Local Network Privacy (loopback), no permissions (no capture). Exits 0 (PASS) / 1.
//
// Usage:
//   dotnet run --project tools/MockStudent -- --selftest
//   dotnet run --project tools/MockStudent -- --ip 172.20.10.5 --port 7777   # connect to a Teacher

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;

string ip = "127.0.0.1";
int port = 7777;
string name = $"MockStudent ({Environment.MachineName})";
bool selfTest = false;
int classroomN = 0, durationSec = 8;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--selftest": selfTest = true; break;
        case "--ip": ip = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--name": name = args[++i]; break;
        case "--classroom": classroomN = int.Parse(args[++i]); break;   // TT-0-D: N replay-clients
        case "--duration": durationSec = int.Parse(args[++i]); break;
    }
}

if (selfTest) return await SelfTest.RunAsync();
if (classroomN > 0) return await Classroom.RunAsync(ip, port, classroomN, durationSec);
return await Interactive.RunAsync(ip, port, name);


// ───────────────────────────── shared framing (stub-server side) ─────────────────────────────
// The CLIENT side is fully handled by WireClient; only the test stub-server needs to frame
// envelopes itself: [4-byte big-endian Int32 length] + MessagePack(Envelope), exactly the shipped
// framing (mirrors MockTeacher).
static class Framing
{
    public static async Task SendAsync(NetworkStream s, MessageType type, byte[] payload, Guid sender, CancellationToken ct = default)
    {
        var env = Envelope.Create(type, payload, sender);
        var body = env.Serialize();
        var frame = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(0, 4), body.Length);
        body.CopyTo(frame, 4);
        await s.WriteAsync(frame, ct);
    }

    public static async Task<Envelope?> ReadAsync(NetworkStream s, CancellationToken ct = default)
    {
        var lenBuf = new byte[4];
        if (await ReadExactAsync(s, lenBuf, 4, ct) == 0) return null;
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
        if (len <= 0 || len > 64 * 1024 * 1024) throw new InvalidOperationException($"bad frame size {len}");
        var body = new byte[len];
        await ReadExactAsync(s, body, len, ct);
        return Envelope.Deserialize(body);
    }

    static async Task<int> ReadExactAsync(NetworkStream s, byte[] buf, int count, CancellationToken ct)
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
}


// ───────────────────────────── the student dispatch loop ─────────────────────────────
// A test client RECEIVES + ACKs (logs) — it does NOT enforce (no LockService/shield, no tray, no
// config). Delivery confirmation is all the Teacher needs from a MockStudent. TT-0-C adds the
// streamer responses (StudentStreamStart → ScreenStreamer, etc.).
static class Dispatch
{
    public static string Describe(Envelope env) => env.Type switch
    {
        MessageType.LockScreen => "🔒 LockScreen — (ack: a real student would raise the shield)",
        MessageType.UnlockScreen => "🔓 UnlockScreen",
        MessageType.PolicyApply => "PolicyApply — (ack)",
        MessageType.PolicyRevert => "PolicyRevert — (ack)",
        MessageType.ChatBroadcast or MessageType.ChatDirect => "Chat — (ack)",
        MessageType.StudentStreamStart => "StudentStreamStart — (TT-0-C: begin ScreenStreamer)",
        MessageType.StudentStreamStop => "StudentStreamStop — (TT-0-C: stop ScreenStreamer)",
        MessageType.Pong => "Pong (heartbeat reply)",
        _ => $"{env.Type} — (ack)",
    };
}


// ───────────────────────────── interactive: connect to a Teacher ─────────────────────────────
static class Interactive
{
    public static async Task<int> RunAsync(string ip, int port, string name)
    {
        Console.WriteLine($"=== MockStudent → {ip}:{port} as \"{name}\" (real streamers on command; Ctrl+C to quit) ===");
        var agent = new StudentAgent(m => Console.WriteLine($"  [rx] {m}"));
        agent.Wire.StatusChanged += s => Console.WriteLine($"  [status] {s}");
        agent.Wire.Traffic += (dir, label, size) => { if (dir == WireDirection.Tx) Console.WriteLine($"  [tx] {label} {size}B"); };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { await agent.Wire.RunAsync(ip, port, name, cts.Token); }
        catch (OperationCanceledException) { }
        return 0;
    }
}


// ───────────────────────────── the student agent: WireClient + REAL streamers ─────────────────────────────
// TT-0-C — mirrors the Student's ConnectionViewModel dispatch EXACTLY (same streamer classes, no
// fork), so MockStudent emits byte-identical wire frames to the real Mac Student. On the Teacher's
// commands it drives ScreenStreamer / CameraStreamer / AudioStreamer; on disconnect it stops all.
// (Streaming does real capture → needs Screen/Camera/Mic grants on THIS binary, same as MockTeacher's
// --streamtest/--cameratest/--audiotest.)
sealed class StudentAgent
{
    public WireClient Wire { get; } = new();
    readonly ScreenStreamer _screen = new();
    readonly CameraStreamer _camera = new();
    readonly AudioStreamer _audio = new();
    readonly Action<string> _log;

    public StudentAgent(Action<string> log)
    {
        _log = log;
        Wire.EnvelopeReceived += env => _ = DispatchAsync(env);
        Wire.StatusChanged += s => { if (s == WireStatus.Disconnected) StopAll(); };
    }

    async Task DispatchAsync(Envelope env)
    {
        try
        {
            switch (env.Type)
            {
                case MessageType.StudentStreamStart:
                    var codec = DecodeCodec(env.Payload);
                    _log($"StudentStreamStart → ScreenStreamer ({codec})");
                    await _screen.StartAsync(Wire, codec);
                    break;
                case MessageType.StudentStreamStop:
                    await _screen.StopAsync(); _log("StudentStreamStop → stop"); break;
                case MessageType.ConferenceStart:
                    var sid = DecodeSession(env.Payload);
                    _log($"ConferenceStart → CameraStreamer ({Short(sid)})");
                    await _camera.StartAsync(Wire, Wire.EndpointId, sid, "MockStudent");
                    break;
                case MessageType.ConferenceEnd:
                    await _camera.StopAsync(); _log("ConferenceEnd → stop"); break;
                case MessageType.MicMonitorStart:
                    _log("MicMonitorStart → AudioStreamer");
                    await _audio.StartAsync(Wire, Wire.EndpointId);
                    break;
                case MessageType.MicMonitorStop:
                    await _audio.StopAsync(); _log("MicMonitorStop → stop"); break;
                default:
                    _log($"received {Dispatch.Describe(env)}"); break;
            }
        }
        catch (Exception ex) { _log($"dispatch error on {env.Type}: {ex.Message}"); }
    }

    void StopAll() { _ = _screen.StopAsync(); _ = _camera.StopAsync(); _ = _audio.StopAsync(); }

    static VideoCodec DecodeCodec(byte[] p)
    { try { return p.Length == 0 ? VideoCodec.Mjpeg : MessagePackSerializer.Deserialize<StudentStreamStartRequest>(p).Codec; } catch { return VideoCodec.Mjpeg; } }
    static Guid DecodeSession(byte[] p)
    { try { return p.Length == 0 ? Guid.Empty : MessagePackSerializer.Deserialize<ConferenceStartMessage>(p).SessionId; } catch { return Guid.Empty; } }
    static string Short(Guid g) => g.ToString()[..8];
}


// ───────────────────────────── TT-0-D: golden capture (1 encode) ─────────────────────────────
// Capture a GOLDEN H.264 sample ONCE (keyframe + a few deltas) via the real ScreenStreamer routed
// to a loopback recorder. Those raw StudentStreamFrame payloads are then replayed VERBATIM by N
// clients — so a classroom does 1 encode + N×(send), leaving the Teacher's decode/render as the
// sole bottleneck. Recording starts at the FIRST keyframe so every replay begins with an IDR (each
// per-stream VTDecompressionSession syncs immediately). Needs Screen Recording once (capture).
static class GoldenSample
{
    public static async Task<(List<byte[]> payloads, int keyframes)> CaptureScreenH264Async(int wantFrames = 8)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var payloads = new List<byte[]>();
        int keyframes = 0;
        var teacherId = Guid.NewGuid();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync();
            var s = conn.GetStream();
            while (payloads.Count < wantFrames)
            {
                var env = await Framing.ReadAsync(s);
                if (env is null) return;
                if (env.Type == MessageType.Hello)
                    await Framing.SendAsync(s, MessageType.StudentStreamStart,
                        MessagePackSerializer.Serialize(new StudentStreamStartRequest { Codec = VideoCodec.H264 }), teacherId);
                else if (env.Type == MessageType.Ping)
                    await Framing.SendAsync(s, MessageType.Pong, Array.Empty<byte>(), teacherId);
                else if (env.Type == MessageType.StudentStreamFrame)
                {
                    var f = MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(env.Payload);
                    if (payloads.Count == 0 && !f.IsKeyframe) continue;   // begin the golden loop on an IDR
                    if (f.IsKeyframe) keyframes++;
                    payloads.Add(env.Payload);
                }
            }
            done.TrySetResult();
        });

        var wire = new WireClient();
        var streamer = new ScreenStreamer();
        wire.EnvelopeReceived += env => { if (env.Type == MessageType.StudentStreamStart) _ = streamer.StartAsync(wire, VideoCodec.H264); };
        using var cts = new CancellationTokenSource();
        var run = wire.RunAsync("127.0.0.1", port, "GoldenCapture", cts.Token);
        await Task.WhenAny(done.Task, Task.Delay(15000));
        await streamer.StopAsync();
        cts.Cancel(); try { await run; } catch { }
        listener.Stop();
        return (payloads, keyframes);
    }
}


// ── one replay-client: WireClient that, on StudentStreamStart, replays the golden frames on a timer
//    (~4 fps, matching the shipped broadcaster). NO capture, NO encode — pure send. ──
sealed class ReplayClient
{
    public WireClient Wire { get; } = new();
    readonly IReadOnlyList<byte[]> _golden;
    CancellationTokenSource? _replayCts;
    public long FramesSent;

    public ReplayClient(IReadOnlyList<byte[]> golden)
    {
        _golden = golden;
        Wire.EnvelopeReceived += env =>
        {
            if (env.Type == MessageType.StudentStreamStart) StartReplay();
            else if (env.Type == MessageType.StudentStreamStop) _replayCts?.Cancel();
        };
        Wire.StatusChanged += s => { if (s == WireStatus.Disconnected) _replayCts?.Cancel(); };
    }

    void StartReplay()
    {
        _replayCts?.Cancel();
        _replayCts = new CancellationTokenSource();
        var ct = _replayCts.Token;
        _ = Task.Run(async () =>
        {
            int idx = 0;
            while (!ct.IsCancellationRequested)
            {
                try { await Wire.SendAsync(MessageType.StudentStreamFrame, _golden[idx], ct); }
                catch { return; }
                Interlocked.Increment(ref FramesSent);
                idx = (idx + 1) % _golden.Count;
                try { await Task.Delay(250, ct); } catch { return; }
            }
        }, ct);
    }
}


// ───────────────────────────── --classroom N: N replay-clients, one process ─────────────────────────────
static class Classroom
{
    public static async Task<int> RunAsync(string ip, int port, int n, int durationSec)
    {
        Console.WriteLine($"=== MockStudent --classroom {n} → {ip}:{port} (1 golden encode, {n} replay-clients, {durationSec}s) ===");

        var (golden, keyframes) = await GoldenSample.CaptureScreenH264Async();
        if (golden.Count == 0 || keyframes == 0)
        {
            Console.WriteLine($"  ❌ golden H.264 capture failed (frames={golden.Count}, keyframes={keyframes}) — is Screen Recording granted?");
            return 1;
        }
        long goldenBytes = golden.Sum(p => (long)p.Length);
        Console.WriteLine($"  golden: {golden.Count} H.264 frames · {keyframes} keyframe(s) · {goldenBytes / 1024}KB — ONE encode, replayed ×{n}");

        var clients = new List<ReplayClient>();
        for (int i = 0; i < n; i++) clients.Add(new ReplayClient(golden));
        using var cts = new CancellationTokenSource();
        var runs = clients.Select((c, i) => c.Wire.RunAsync(ip, port, $"Replay-{i:D2}", cts.Token)).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastFrames = 0;
        while (sw.Elapsed.TotalSeconds < durationSec)
        {
            await Task.Delay(2000);
            int connected = clients.Count(c => c.Wire.Status == WireStatus.Connected);
            long total = clients.Sum(c => c.FramesSent);
            double fps = (total - lastFrames) / 2.0; lastFrames = total;
            Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:0}s] connected={connected}/{n} · frames sent={total} · ~{fps:0} fps aggregate");
        }

        int finalConnected = clients.Count(c => c.Wire.Status == WireStatus.Connected);
        long finalFrames = clients.Sum(c => c.FramesSent);
        cts.Cancel();
        try { await Task.WhenAll(runs); } catch { }

        bool ok = finalConnected == n && finalFrames > 0;
        Console.WriteLine(ok
            ? $"\n=== CLASSROOM {n} ✅ — {finalConnected}/{n} clients connected · {finalFrames} frames delivered (1 encode → {n} streams) ==="
            : $"\n=== CLASSROOM {n} ❌ — {finalConnected}/{n} connected · {finalFrames} frames ===");
        return ok ? 0 : 1;
    }
}


// ───────────────────────────── --selftest: in-proc teacher-stub, loopback ─────────────────────────────
// Proves the MockStudent harness end-to-end with ZERO external dependencies: a minimal teacher-stub
// on a loopback port drives the REAL WireClient through connect → Hello → command-receipt →
// heartbeat Ping/Pong. Loopback ⇒ no Local Network Privacy gate; no capture ⇒ no permissions.
static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;
        void Check(string label, bool ok)
        {
            if (ok) Console.WriteLine($"  ✅ {label}");
            else { Console.WriteLine($"  ❌ {label}"); failures++; }
        }

        Console.WriteLine("=== MockStudent --selftest (in-proc teacher-stub, loopback — no LNP, no perms) ===");

        var teacherId = Guid.NewGuid();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var helloTcs = new TaskCompletionSource<HelloMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pingTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkStream? serverStream = null;

        using var serverCts = new CancellationTokenSource();
        var serverTask = Task.Run(async () =>
        {
            using var conn = await listener.AcceptTcpClientAsync(serverCts.Token);
            serverStream = conn.GetStream();
            while (!serverCts.IsCancellationRequested)
            {
                Envelope? env;
                try { env = await Framing.ReadAsync(serverStream, serverCts.Token); }
                catch { return; }
                if (env is null) return;   // client closed
                if (env.Type == MessageType.Hello)
                    helloTcs.TrySetResult(MessagePackSerializer.Deserialize<HelloMessage>(env.Payload));
                else if (env.Type == MessageType.Ping)
                {
                    pingTcs.TrySetResult();
                    await Framing.SendAsync(serverStream, MessageType.Pong, Array.Empty<byte>(), teacherId, serverCts.Token);
                }
            }
        }, serverCts.Token);

        // The real student-side transport, driven headlessly.
        var client = new WireClient();
        var received = new ConcurrentBag<MessageType>();
        client.EnvelopeReceived += env => { received.Add(env.Type); Console.WriteLine($"        [student] received {env.Type}"); };
        using var clientCts = new CancellationTokenSource();
        var run = client.RunAsync("127.0.0.1", port, "SelfTest MockStudent", clientCts.Token);

        // (1) connect → Hello
        var hello = await WaitValue(helloTcs.Task, 5000);
        Check("stub-teacher received the student's Hello", hello is not null);
        Check("Hello carries the client's EndpointId", hello?.EndpointId == client.EndpointId);
        Check("client reports Connected", client.Status == WireStatus.Connected);

        // (2) teacher → command → the student's dispatch receives it
        if (serverStream is not null)
            await Framing.SendAsync(serverStream, MessageType.LockScreen, Array.Empty<byte>(), teacherId);
        Check("client received the LockScreen command", await WaitUntil(() => received.Contains(MessageType.LockScreen), 3000));

        // (3) heartbeat: the client Pings within ~5 s; the stub replies Pong → link stays up
        Check("stub-teacher received the client's heartbeat Ping", await WaitTask(pingTcs.Task, 8000));
        await Task.Delay(300);
        Check("client STILL Connected after Ping/Pong", client.Status == WireStatus.Connected);

        clientCts.Cancel();
        serverCts.Cancel();
        try { await run; } catch { }
        try { await serverTask; } catch { }
        listener.Stop();

        Console.WriteLine(failures == 0
            ? "\n=== MOCKSTUDENT SELFTEST PASS ✅ — connect → Hello → command receipt → heartbeat Ping/Pong ==="
            : $"\n=== MOCKSTUDENT SELFTEST FAIL ❌ ({failures} check(s)) ===");
        return failures == 0 ? 0 : 1;
    }

    static async Task<T?> WaitValue<T>(Task<T> task, int ms) where T : class
        => await Task.WhenAny(task, Task.Delay(ms)) == task ? task.Result : null;

    static async Task<bool> WaitTask(Task task, int ms)
        => await Task.WhenAny(task, Task.Delay(ms)) == task && task.IsCompletedSuccessfully;

    static async Task<bool> WaitUntil(Func<bool> cond, int ms)
    {
        for (int t = 0; t < ms && !cond(); t += 50) await Task.Delay(50);
        return cond();
    }
}
