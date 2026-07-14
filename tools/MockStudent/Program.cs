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
//   --teacherselftest — TT-1-E: the inverse — spin the REAL ClassroomCtrl.Teacher.Core
//             (ControlServer + StudentRoster) on loopback and drive the real WireClient + raw
//             connections through roster identity, Ping/Pong, the 3-student middle-disconnect fix,
//             the reconnect-ownership guard, and stale-sweep — guaranteed teardown. The permanent
//             Teacher-side gate. Loopback ⇒ no LNP; no capture ⇒ no perms. Exits 0 (PASS) / 1.
//
// Usage:
//   dotnet run --project tools/MockStudent -- --selftest
//   dotnet run --project tools/MockStudent -- --teacherselftest
//   dotnet run --project tools/MockStudent -- --ip 172.20.10.5 --port 7777   # connect to a Teacher

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Teacher.Services;                 // TT-1-E: real ControlServer
using ClassroomCtrl.Teacher.Core;                     // TT-1-E: real StudentRoster
using Microsoft.Extensions.Logging.Abstractions;      // TT-1-E: NullLogger for the headless server
using MessagePack;

string ip = "127.0.0.1";
int port = 7777;
string name = $"MockStudent ({Environment.MachineName})";
bool selfTest = false;
bool teacherSelfTest = false;
int classroomN = 0, durationSec = 8;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--selftest": selfTest = true; break;
        case "--teacherselftest": teacherSelfTest = true; break;   // TT-1-E: real Teacher.Core gate
        case "--ip": ip = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--name": name = args[++i]; break;
        case "--classroom": classroomN = int.Parse(args[++i]); break;   // TT-0-D: N replay-clients
        case "--duration": durationSec = int.Parse(args[++i]); break;
    }
}

if (teacherSelfTest) return await TeacherSelfTest.RunAsync();
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

// ───────────────────────────── TT-1-E: --teacherselftest — the REAL Teacher.Core gate ─────────────────────────────
// The permanent Teacher-side gate (the analog of the Student track's --selftest / --locktest).
// Spins the REAL ControlServer + StudentRoster from ClassroomCtrl.Teacher.Core on 127.0.0.1:0
// (ephemeral → no :7777 collision, not LAN-visible, no Local Network Privacy) and proves:
//   • the REAL student WireClient (the same client the Mac Student ships) → roster identity +
//     Ping/Pong liveness + clean-disconnect removal, and the 3-student middle-disconnect fix;
//   • raw framed connections (deterministic socket / EndpointId control) → the reconnect-ownership
//     guard and stale-sweep eviction.
// Guaranteed teardown: each server runs under try/finally + a hard 30 s timeout, and every scenario
// asserts the ephemeral port re-binds after Dispose (zero listening sockets left behind).
static class TeacherSelfTest
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;
        void Check(string label, bool ok)
        {
            if (ok) Console.WriteLine($"  ✅ {label}");
            else { Console.WriteLine($"  ❌ {label}"); failures++; }
        }

        Console.WriteLine("=== MockStudent --teacherselftest (REAL Teacher.Core on loopback — no LNP, no perms) ===");

        // ── (0) TT-5 COMMAND WIRING — the send-path rule, now PERMANENT (promoted from the
        //    scratchpad TT5Gate). Drives the REAL StudentCommandController (Teacher.Core) through
        //    a recording fake sink and asserts the CHANNEL — reliable==true for EVERY command —
        //    not just that a send occurred. A send-only check passes with the shipped v1.2.1
        //    per-student lossy bug present (the command IS sent — it's evicted later under load),
        //    so the distinguishing assertion is the channel. Also covers the platform power-gate
        //    and the confirm policy. Pure logic (no socket) — the UI-agnostic seam lives in Core.
        Console.WriteLine("-- (0) TT-5 command wiring: StudentCommandController + recording fake sink --");
        {
            // platform power-gate: Windows → offer; everything else → deny (default-deny on "Windows")
            Check("platform: Windows → power OFFERED", StudentPlatform.CanReceivePower("Microsoft Windows NT 10.0.19045.0"));
            Check("platform: macOS → power DENIED", !StudentPlatform.CanReceivePower("macOS 26.5.2"));
            Check("platform: Unix → power DENIED", !StudentPlatform.CanReceivePower("Unix 26.5.2"));
            Check("platform: Linux → power DENIED", !StudentPlatform.CanReceivePower("Linux 6.1.0"));
            Check("platform: empty → power DENIED (default-deny)", !StudentPlatform.CanReceivePower(""));
            Check("platform: null → power DENIED (default-deny)", !StudentPlatform.CanReceivePower(null));
            Check("platform: whitespace → power DENIED", !StudentPlatform.CanReceivePower("   "));
            Check("platform: case-insensitive 'windows' → OFFERED", StudentPlatform.CanReceivePower("custom windows image"));

            // send-path rule: EVERY command routed on the reliable channel (assert the CHANNEL)
            var sink = new RecordingCommandSink();
            var ctl = new StudentCommandController(sink);   // default confirm = auto-yes
            var idLock = Guid.NewGuid();
            await ctl.ExecuteAsync(idLock, StudentCommand.Lock, "Alice");
            Check("Lock → LockAsync(locked:true, reliable:TRUE), correct endpoint",
                sink.Last is RecordingCommandSink.LockCall { Locked: true, Reliable: true } lk && lk.Endpoint == idLock);
            var idUnlock = Guid.NewGuid();
            await ctl.ExecuteAsync(idUnlock, StudentCommand.Unlock, "Bob");
            Check("Unlock → LockAsync(locked:false, reliable:TRUE), correct endpoint",
                sink.Last is RecordingCommandSink.LockCall { Locked: false, Reliable: true } ul && ul.Endpoint == idUnlock);
            foreach (var (cmd, type) in new[]
            {
                (StudentCommand.Logoff, MessageType.ForceLogoff),
                (StudentCommand.Restart, MessageType.ForceRestart),
                (StudentCommand.Shutdown, MessageType.ForceShutdown),
            })
            {
                var id = Guid.NewGuid();
                await ctl.ExecuteAsync(id, cmd, "Cara");
                Check($"{cmd} → PowerAsync({type}, reliable:TRUE), correct endpoint",
                    sink.Last is RecordingCommandSink.PowerCall pc && pc.Type == type && pc.Reliable && pc.Endpoint == id);
            }
            Check("NO command routed on the LOSSY channel (every reliable==true)", sink.AllReliable);

            // confirmation policy: power gated; lock/unlock never prompt
            var declineSink = new RecordingCommandSink();
            var declineCtl = new StudentCommandController(declineSink, confirmAsync: _ => Task.FromResult(false));
            await declineCtl.ExecuteAsync(Guid.NewGuid(), StudentCommand.Shutdown, "Dan");
            Check("power confirm=NO → NOTHING sent", declineSink.Count == 0);
            var acceptSink = new RecordingCommandSink();
            int powerPrompts = 0;
            var acceptCtl = new StudentCommandController(acceptSink, confirmAsync: _ => { powerPrompts++; return Task.FromResult(true); });
            await acceptCtl.ExecuteAsync(Guid.NewGuid(), StudentCommand.Shutdown, "Dan");
            Check("power confirm=YES → sent on the RELIABLE channel",
                acceptSink.Last is RecordingCommandSink.PowerCall { Reliable: true, Type: MessageType.ForceShutdown });
            Check("power prompted exactly once", powerPrompts == 1);
            int lockPrompts = 0;
            var noPromptCtl = new StudentCommandController(new RecordingCommandSink(), confirmAsync: _ => { lockPrompts++; return Task.FromResult(true); });
            await noPromptCtl.ExecuteAsync(Guid.NewGuid(), StudentCommand.Lock, "X");
            await noPromptCtl.ExecuteAsync(Guid.NewGuid(), StudentCommand.Unlock, "X");
            Check("lock/unlock NEVER prompt for confirmation", lockPrompts == 0);

            // prompt text + power mapping sanity
            Check("confirm prompt names the student", StudentCommandController.ConfirmPrompt(StudentCommand.Shutdown, "Zed").Contains("Zed"));
            Check("logoff/restart/shutdown prompts distinct",
                StudentCommandController.ConfirmPrompt(StudentCommand.Logoff, "n") != StudentCommandController.ConfirmPrompt(StudentCommand.Restart, "n")
                && StudentCommandController.ConfirmPrompt(StudentCommand.Restart, "n") != StudentCommandController.ConfirmPrompt(StudentCommand.Shutdown, "n"));
            Check("ToMessageType(Logoff)=ForceLogoff", StudentCommandController.ToMessageType(StudentCommand.Logoff) == MessageType.ForceLogoff);
            Check("ToMessageType(Restart)=ForceRestart", StudentCommandController.ToMessageType(StudentCommand.Restart) == MessageType.ForceRestart);
            Check("ToMessageType(Shutdown)=ForceShutdown", StudentCommandController.ToMessageType(StudentCommand.Shutdown) == MessageType.ForceShutdown);
        }

        // ── (0b) TT-6-B SELECTION MODEL — the UI-agnostic TileSelectionModel (Teacher.Core),
        //    committed here (not scratchpad). Covers the macOS click idiom (plain=select-only,
        //    ⌘=toggle, Shift=range), select-all/clear, disconnect-prune, AND the distinguishing
        //    property: Snapshot() is a COPY, stable across a mid-loop disconnect — a naive model
        //    (live view) corrupts a bulk op here.
        Console.WriteLine("-- (0b) TT-6 selection model (TileSelectionModel) --");
        {
            var a = Guid.NewGuid(); var b = Guid.NewGuid(); var c = Guid.NewGuid();
            var d = Guid.NewGuid(); var e = Guid.NewGuid();
            var ordered = new List<Guid> { a, b, c, d, e };
            var sel = new TileSelectionModel();

            sel.SelectOnly(a);
            Check("plain click → selects only that tile", sel.Count == 1 && sel.IsSelected(a));
            sel.SelectOnly(b);
            Check("plain click again → deselects the previous (macOS idiom)", sel.Count == 1 && sel.IsSelected(b) && !sel.IsSelected(a));

            sel.Clear(); sel.Toggle(a);
            Check("⌘-click → adds to selection", sel.IsSelected(a) && sel.Count == 1);
            sel.Toggle(a);
            Check("⌘-click again → removes from selection", !sel.IsSelected(a) && sel.Count == 0);
            sel.SelectOnly(a); sel.Toggle(b);
            Check("⌘-click extends a multi-selection", sel.Count == 2 && sel.IsSelected(a) && sel.IsSelected(b));

            sel.Clear(); sel.SelectOnly(b);            // anchor = b
            sel.SelectRange(ordered, d);
            Check("Shift-range → contiguous anchor..target inclusive", sel.Count == 3 && sel.IsSelected(b) && sel.IsSelected(c) && sel.IsSelected(d));
            sel.SelectRange(ordered, a);               // re-range from the SAME anchor b
            Check("Shift-range re-ranges from the fixed anchor", sel.Count == 2 && sel.IsSelected(a) && sel.IsSelected(b) && !sel.IsSelected(d));

            sel.SelectAll(ordered);
            Check("select-all selects every tile", sel.Count == 5);
            sel.Clear();
            Check("clear deselects everything", sel.Count == 0 && !sel.HasSelection);

            // HandleClick dispatch (the decoded-modifier entry point the window calls)
            sel.Clear();
            sel.HandleClick(a, cmdKey: false, shiftKey: false, ordered);
            Check("HandleClick(plain) → SelectOnly", sel.Count == 1 && sel.IsSelected(a));
            sel.HandleClick(b, cmdKey: true, shiftKey: false, ordered);
            Check("HandleClick(⌘) → Toggle (multi)", sel.Count == 2 && sel.IsSelected(a) && sel.IsSelected(b));
            sel.HandleClick(d, cmdKey: false, shiftKey: true, ordered);   // anchor is b (last)
            Check("HandleClick(Shift) → range from anchor", sel.Count == 3 && sel.IsSelected(b) && sel.IsSelected(c) && sel.IsSelected(d) && !sel.IsSelected(a));

            // disconnect prune
            sel.Clear(); sel.SelectOnly(a); sel.Toggle(b); sel.Toggle(c);   // {a,b,c}
            sel.Prune(new List<Guid> { a, c });                            // b disconnected
            Check("prune drops a disconnected student's selection", sel.Count == 2 && sel.IsSelected(a) && sel.IsSelected(c) && !sel.IsSelected(b));
            sel.Clear(); sel.SelectOnly(b);                                 // anchor = b
            sel.Prune(new List<Guid> { a, c });                            // anchor b gone
            sel.HandleClick(d, cmdKey: false, shiftKey: true, ordered);    // no anchor → degrade to SelectOnly
            Check("prune clears a stale anchor (range degrades to select-only)", sel.Count == 1 && sel.IsSelected(d));

            // THE DISTINGUISHING TEST — snapshot is a copy, stable across a mid-loop disconnect
            sel.Clear(); sel.SelectOnly(a); sel.Toggle(b); sel.Toggle(c);  // {a,b,c}
            var snap = sel.Snapshot();
            sel.Prune(new List<Guid> { a });                               // b, c disconnect mid-"loop"
            Check("snapshot is STABLE across a mid-loop disconnect (copy, not live view)",
                snap.Count == 3 && sel.Count == 1);
        }

        // ── (0c) TT-6-C BULK — ExecuteBulkAsync fans out on the RELIABLE channel (so the guard
        //    covers bulk BY CONSTRUCTION), skips non-Windows students for power (decision A), and
        //    confirms once (decision B). Assert the CHANNEL + the skip + the confirm — a send-only
        //    check would miss a bulk command regressing to the lossy path.
        Console.WriteLine("-- (0c) TT-6 bulk (StudentCommandController.ExecuteBulkAsync) --");
        {
            var win1 = new BulkTarget(Guid.NewGuid(), CanReceivePower: true);
            var win2 = new BulkTarget(Guid.NewGuid(), CanReceivePower: true);
            var mac = new BulkTarget(Guid.NewGuid(), CanReceivePower: false);

            var s1 = new RecordingCommandSink();
            var r1 = await new StudentCommandController(s1).ExecuteBulkAsync(new[] { win1, win2, mac }, StudentCommand.Lock);
            Check("bulk lock → all 3 on the RELIABLE channel", s1.Count == 3 && s1.AllReliable && r1.Sent == 3 && r1.Skipped == 0);

            var s2 = new RecordingCommandSink();
            var r2 = await new StudentCommandController(s2, confirmAsync: _ => Task.FromResult(true))
                .ExecuteBulkAsync(new[] { win1, win2, mac }, StudentCommand.Shutdown);
            Check("bulk power (mixed) → Windows only, Mac SKIPPED, all reliable",
                s2.Count == 2 && s2.AllReliable && r2.Sent == 2 && r2.Skipped == 1);

            var s3 = new RecordingCommandSink();
            var r3 = await new StudentCommandController(s3, confirmAsync: _ => Task.FromResult(false))
                .ExecuteBulkAsync(new[] { win1, win2 }, StudentCommand.Logoff);
            Check("bulk power confirm=NO → NOTHING sent (cancelled)", s3.Count == 0 && r3.Cancelled && r3.Sent == 0);

            var s4 = new RecordingCommandSink();
            int prompts4 = 0;
            var r4 = await new StudentCommandController(s4, confirmAsync: _ => { prompts4++; return Task.FromResult(true); })
                .ExecuteBulkAsync(new[] { mac }, StudentCommand.Shutdown);
            Check("bulk power all-macOS → nothing sent, all skipped, never prompted",
                s4.Count == 0 && r4.Sent == 0 && r4.Skipped == 1 && !r4.Cancelled && prompts4 == 0);

            Check("bulk confirm prompt is count-aware", StudentCommandController.BulkConfirmPrompt(StudentCommand.Shutdown, 2).Contains("2 students"));
        }

        // ── Server 1: the REAL student WireClient against the REAL server. ──
        // Large stale window (8 s > the client's 5 s heartbeat) so healthy clients never false-stale.
        await WithServer(8000, Check, "server-1 (real WireClient)", async (server, roster, port) =>
        {
            // (1) Interop: a real WireClient appears in the roster with its identity.
            var (r, rCts, rRun) = StartClient(port, "Live MockStudent");
            Check("real WireClient reaches Connected", await WaitUntil(() => r.Status == WireStatus.Connected, 5000));
            Check("real WireClient appears in the roster", await WaitUntil(() => roster.Contains(r.EndpointId), 5000));
            roster.TryGet(r.EndpointId, out var entry);
            Check("roster carries EndpointId + DisplayName + MachineName",
                entry is not null && entry.DisplayName == "Live MockStudent" && entry.MachineName == Environment.MachineName);

            // Ping/Pong liveness through the real client (drive one Ping immediately for speed).
            int pongs = 0;
            r.EnvelopeReceived += e => { if (e.Type == MessageType.Pong) Interlocked.Increment(ref pongs); };
            await r.SendAsync(MessageType.Ping, Array.Empty<byte>(), CancellationToken.None);
            Check("real client Ping → server Pong (liveness)", await WaitUntil(() => Volatile.Read(ref pongs) > 0, 3000));

            // (1b) TT-5-B: per-student COMMAND DELIVERY over the real transport. The
            // Teacher's LockOne / PowerOne must reach the TARGETED student with the right
            // MessageType and TargetEndpointId. This is the permanent end-to-end guard for
            // TT-5's command path (lock/unlock + power). NOTE: the reliable-vs-lossy CHANNEL
            // choice is NOT observable over a quiescent loopback (both deliver) — the
            // TT5Gate fake-sink gate asserts reliable:true is chosen. Here we prove the
            // delivery + targeting the shipped Windows per-student path also relies on.
            int lockRx = 0, logoffRx = 0;
            r.EnvelopeReceived += e =>
            {
                if (e.Type == MessageType.LockScreen && e.TargetEndpointId == r.EndpointId) Interlocked.Increment(ref lockRx);
                if (e.Type == MessageType.ForceLogoff && e.TargetEndpointId == r.EndpointId) Interlocked.Increment(ref logoffRx);
            };
            await server.LockOneAsync(r.EndpointId, locked: true, CancellationToken.None, reliable: true);
            Check("LockOne(reliable) → targeted student receives LockScreen (targeted)",
                await WaitUntil(() => Volatile.Read(ref lockRx) > 0, 3000));
            await server.PowerOneAsync(r.EndpointId, MessageType.ForceLogoff, CancellationToken.None, reliable: true);
            Check("PowerOne(reliable, ForceLogoff) → targeted student receives ForceLogoff (targeted)",
                await WaitUntil(() => Volatile.Read(ref logoffRx) > 0, 3000));

            // Clean disconnect removes it from the roster.
            rCts.Cancel(); try { await rRun.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            Check("clean disconnect removes the student from the roster", await WaitUntil(() => !roster.Contains(r.EndpointId), 3000));

            // (2) 3 REAL WireClients → disconnect the MIDDLE one → the RIGHT one is removed.
            var a = StartClient(port, "Alice"); var b = StartClient(port, "Bob"); var c = StartClient(port, "Cara");
            Check("3 real students joined", await WaitUntil(() => roster.Count == 3, 5000));
            b.cts.Cancel(); try { await b.run.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            Check("disconnecting the MIDDLE student removes B", await WaitUntil(() => !roster.Contains(b.client.EndpointId), 3000));
            Check("A and C REMAIN — buggy RemoveAt(Count-1) would have dropped C",
                roster.Contains(a.client.EndpointId) && roster.Contains(c.client.EndpointId) && roster.Count == 2);
            a.cts.Cancel(); c.cts.Cancel();
            try { await Task.WhenAll(a.run, c.run).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        });

        // ── Server 2: raw framed clients for deterministic socket / EndpointId control. ──
        // Short stale window (600 ms) + a 150 ms ping-keeper → the stale-sweep test runs in ~2 s.
        await WithServer(600, Check, "server-2 (raw client)", async (server, roster, port) =>
        {
            var A = Guid.NewGuid(); var B = Guid.NewGuid(); var C = Guid.NewGuid();
            var alive = new Dictionary<TcpClient, Guid>(); var gate = new object();
            void Keep(TcpClient cl, Guid id) { lock (gate) alive[cl] = id; }
            void Drop(TcpClient cl) { lock (gate) alive.Remove(cl); }
            using var pingCts = new CancellationTokenSource();
            var pinger = Task.Run(async () =>
            {
                while (!pingCts.IsCancellationRequested)
                {
                    KeyValuePair<TcpClient, Guid>[] snap; lock (gate) snap = alive.ToArray();
                    foreach (var kv in snap) { try { await Framing.SendAsync(kv.Key.GetStream(), MessageType.Ping, Array.Empty<byte>(), kv.Value); } catch { } }
                    try { await Task.Delay(150, pingCts.Token); } catch { }
                }
            });
            async Task<TcpClient> Raw(Guid id, string name)
            {
                var cl = new TcpClient(); await cl.ConnectAsync(IPAddress.Loopback, port);
                var hello = new HelloMessage { EndpointId = id, DisplayName = name, MachineName = "mac-raw" };
                await Framing.SendAsync(cl.GetStream(), MessageType.Hello, MessagePackSerializer.Serialize(hello), id);
                Keep(cl, id); return cl;
            }

            var ca = await Raw(A, "Alice"); var cb = await Raw(B, "Bob"); var cc = await Raw(C, "Cara");
            Check("raw: 3 students joined", await WaitUntil(() => roster.Count == 3, 3000));

            // middle disconnect + reconnect (same EndpointId) → A, C undisturbed
            Drop(cb); cb.Close();
            Check("raw: middle disconnect removes B; A + C remain",
                await WaitUntil(() => !roster.Contains(B) && roster.Contains(A) && roster.Contains(C), 3000));
            var cb2 = await Raw(B, "Bob");
            Check("raw: B reconnects → roster A, C, B", await WaitUntil(() => roster.Count == 3 && roster.Contains(B), 3000));

            // ownership guard: cb3 takes over endpoint B; dropping the OLD cb2 must not evict B
            var cb3 = await Raw(B, "Bob");
            await WaitUntil(() => roster.Contains(B), 2000);
            Drop(cb2); cb2.Close(); await Task.Delay(500);
            Check("ownership guard: stale old-socket drop does NOT evict reconnected B",
                roster.Contains(B) && roster.Count == 3);

            // stale-sweep: silence B (cb3) → evicted; A / C kept alive → survive
            Drop(cb3);
            Check("stale-sweep evicts the SILENT student B", await WaitUntil(() => !roster.Contains(B), 5000));
            Check("A and C survive the sweep (kept alive)", roster.Contains(A) && roster.Contains(C));

            pingCts.Cancel(); try { await pinger; } catch { }
            ca.Close(); cc.Close(); cb3.Close();
        });

        Console.WriteLine(failures == 0
            ? "\n=== MOCKSTUDENT TEACHERSELFTEST PASS ✅ — command wiring (reliable channel) + real-client interop + roster fix + command delivery + ownership guard + stale-sweep + guaranteed teardown ==="
            : $"\n=== MOCKSTUDENT TEACHERSELFTEST FAIL ❌ ({failures} check(s)) ===");
        return failures == 0 ? 0 : 1;
    }

    // A real student transport (the same WireClient the Mac Student ships), driven headlessly.
    static (WireClient client, CancellationTokenSource cts, Task run) StartClient(int port, string name)
    {
        var c = new WireClient();
        var cts = new CancellationTokenSource();
        var run = c.RunAsync("127.0.0.1", port, name, cts.Token);
        return (c, cts, run);
    }

    // Runs `body` against a REAL ControlServer + StudentRoster on 127.0.0.1:0, then GUARANTEES
    // teardown (Dispose under a hard 30 s timeout) and asserts the port re-binds (no socket leaked) —
    // the server's analog of the Student track's "zero real taps left installed".
    static async Task WithServer(int staleAfterMs, Action<string, bool> check, string label,
        Func<ControlServer, StudentRoster, int, Task> body)
    {
        var server = new ControlServer(NullLogger<ControlServer>.Instance, NullLoggerFactory.Instance,
            IPAddress.Loopback, 0, IPAddress.Loopback, staleAfterMs);
        var roster = new StudentRoster(server);
        int port = -1;
        try
        {
            await server.StartAsync(CancellationToken.None);
            port = server.BoundPort ?? -1;
            check($"{label}: bound an ephemeral port", port > 0);
            await body(server, roster, port).WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception ex) { check($"{label}: scenario threw — {ex.GetType().Name}: {ex.Message}", false); }
        finally { server.Dispose(); }

        await Task.Delay(150);
        bool rebind = false;
        if (port > 0) { try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); rebind = true; } catch { } }
        check($"{label}: Dispose released the listener (re-bind succeeds, zero sockets left)", rebind);
        check($"{label}: BoundPort null after Dispose", server.BoundPort is null);
    }

    static async Task<bool> WaitUntil(Func<bool> cond, int ms)
    {
        for (int t = 0; t < ms && !cond(); t += 25) await Task.Delay(25);
        return cond();
    }
}

// TT-5 (promoted from scratchpad TT5Gate) — a recording fake IStudentCommandSink for the
// command-wiring assertions: captures the reliable CHANNEL of every command so
// --teacherselftest can assert reliable==true PERMANENTLY (the channel is what the shipped
// v1.2.1 per-student path gets wrong). No transport — pure capture.
sealed class RecordingCommandSink : IStudentCommandSink
{
    public abstract record Call(Guid Endpoint, bool Reliable);
    public sealed record LockCall(Guid Endpoint, bool Locked, bool Reliable) : Call(Endpoint, Reliable);
    public sealed record PowerCall(Guid Endpoint, MessageType Type, bool Reliable) : Call(Endpoint, Reliable);

    private readonly List<Call> _calls = new();
    public int Count => _calls.Count;
    public Call? Last => _calls.Count > 0 ? _calls[^1] : null;
    public bool AllReliable => _calls.TrueForAll(c => c.Reliable);

    public Task LockAsync(Guid endpointId, bool locked, bool reliable, CancellationToken ct)
    {
        _calls.Add(new LockCall(endpointId, locked, reliable));
        return Task.CompletedTask;
    }

    public Task PowerAsync(Guid endpointId, MessageType type, bool reliable, CancellationToken ct)
    {
        _calls.Add(new PowerCall(endpointId, type, reliable));
        return Task.CompletedTask;
    }
}
