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
using ClassroomCtrl.Avalonia.Teacher.ViewModels;      // TT-7-E: TeacherGridViewModel / ChatViewModel attribution
using ClassroomCtrl.Avalonia.Teacher.Services;        // TT-7-E: ITeacherMessaging / ISoundService / NotificationSound
using Microsoft.Extensions.Logging.Abstractions;      // TT-1-E: NullLogger for the headless server
using MessagePack;

string ip = "127.0.0.1";
int port = 7777;
string name = $"MockStudent ({Environment.MachineName})";
bool selfTest = false;
bool teacherSelfTest = false;
int classroomN = 0, durationSec = 8;
bool classroomAudio = false;
int mixtestN = 0;
bool mixhost = false;
bool fileTest = false;
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
        case "--audio": classroomAudio = true; break;                    // TT-9-B: also stream synthetic mic PCM
        case "--mixtest": mixtestN = int.Parse(args[++i]); break;        // TT-9-D: mixer stall+cap gate
        case "--mixhost": mixhost = true; break;                          // TT-9-D: teacher-only CPU host
        case "--filetest": fileTest = true; break;                        // TT-12: file-transfer E2E gate
    }
}

if (fileTest) return await FileTest.RunAsync();
if (teacherSelfTest) return await TeacherSelfTest.RunAsync();
if (selfTest) return await SelfTest.RunAsync();
if (mixhost) return await MixHost.RunAsync(port, durationSec);
if (mixtestN > 0) return await MixTest.RunAsync(mixtestN, durationSec);
if (classroomN > 0) return await Classroom.RunAsync(ip, port, classroomN, durationSec, classroomAudio);
return await Interactive.RunAsync(ip, port, name);


// ───────────────────────────── shared framing (stub-server side) ─────────────────────────────
// The CLIENT side is fully handled by WireClient; only the test stub-server needs to frame
// envelopes itself: [4-byte big-endian Int32 length] + MessagePack(Envelope), exactly the shipped
// framing (mirrors MockTeacher).
static class Framing
{
    public static async Task SendAsync(NetworkStream s, MessageType type, byte[] payload, Guid sender, CancellationToken ct = default)
        => await SendAsync(s, Envelope.Create(type, payload, sender), ct);

    // TT-6-D — send a pre-built envelope (e.g. CreateTargeted) so the self-tests can exercise
    // the receive-side target filter with real targeted frames.
    public static async Task SendAsync(NetworkStream s, Envelope env, CancellationToken ct = default)
    {
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
    int _teacherAudioFrames;   // TT-10-B LIVE receipt counter

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
                // TT-10-B LIVE — count the teacher's Talk broadcast (receipt evidence for the
                // second student without playing it; the Sandbox student plays it audibly).
                case MessageType.AudioStreamStart:
                    _teacherAudioFrames = 0;
                    _log("🎙 teacher TALK started (AudioStreamStart) — counting frames"); break;
                case MessageType.AudioStreamFrame:
                    if (Interlocked.Increment(ref _teacherAudioFrames) % 25 == 1)
                        _log($"🎙 teacher audio frame #{_teacherAudioFrames} received"); break;
                case MessageType.AudioStreamStop:
                    _log($"🎙 teacher TALK stopped (AudioStreamStop) — {_teacherAudioFrames} frames total"); break;
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
    readonly int _toneHz;                 // TT-9-B: a distinct tone per seat → the teacher-side mix is
                                          //         separable, and the kill-one-sender stall test can
                                          //         name exactly which stream it dropped.
    readonly bool _autoAudio;             // self-start the synthetic mic on connect (load generator)
    CancellationTokenSource? _replayCts;
    CancellationTokenSource? _audioCts;
    public long FramesSent;
    public long AudioFramesSent;

    public ReplayClient(IReadOnlyList<byte[]> golden, int toneHz = 440, bool autoAudio = false)
    {
        _golden = golden;
        _toneHz = toneHz;
        _autoAudio = autoAudio;
        Wire.EnvelopeReceived += env =>
        {
            switch (env.Type)
            {
                case MessageType.StudentStreamStart: StartReplay(); break;
                case MessageType.StudentStreamStop: _replayCts?.Cancel(); break;
                case MessageType.MicMonitorStart: StartAudio(); break;   // TT-9-B: teacher opened my mic (faithful trigger)
                case MessageType.MicMonitorStop: StopAudio(); break;
            }
        };
        Wire.StatusChanged += s =>
        {
            if (s == WireStatus.Disconnected) { _replayCts?.Cancel(); StopAudio(); }
            else if (s == WireStatus.Connected && _autoAudio) StartAudio();
        };
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

    // TT-9-B — the synthetic mic. StudentAudioStreamStart (0x032B) then continuous 100 ms
    // StudentAudioStreamFrame (0x032C) PCM (16 kHz mono 16-bit, 3200 B) — a sine at _toneHz.
    // NO VAD / silence gate: continuous once the mic is open, matching the shipped student
    // (StudentAudioBroadcaster). Idempotent — a second Start (MicMonitorStart + auto) is a no-op.
    public void StartAudio()
    {
        if (_audioCts is { IsCancellationRequested: false }) return;
        _audioCts = new CancellationTokenSource();
        var ct = _audioCts.Token;
        _ = Task.Run(async () =>
        {
            try { await Wire.SendAsync(MessageType.StudentAudioStreamStart, Array.Empty<byte>(), ct); }
            catch { return; }
            int seq = 0;
            while (!ct.IsCancellationRequested)
            {
                var msg = new AudioStreamFrameMessage
                {
                    PcmData = Tone(_toneHz, seq),
                    SampleRate = 16000, Channels = 1, BitsPerSample = 16,
                    TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    FrameSeq = seq + 1,
                };
                try { await Wire.SendAsync(MessageType.StudentAudioStreamFrame, MessagePackSerializer.Serialize(msg), ct); }
                catch { return; }
                Interlocked.Increment(ref AudioFramesSent);
                seq++;
                try { await Task.Delay(100, ct); } catch { return; }
            }
        }, ct);
    }

    public void StopAudio()
    {
        var cts = _audioCts; _audioCts = null;
        if (cts is null) return;
        cts.Cancel();
        try { _ = Wire.SendAsync(MessageType.StudentAudioStreamStop, Array.Empty<byte>(), CancellationToken.None); } catch { }
    }

    /// <summary>TT-9-D — simulate a NETWORK STALL: stop the audio pump WITHOUT sending
    /// StudentAudioStreamStop, so the teacher's mixer keeps the source REGISTERED and its node
    /// starves. This is the true non-blocking-invariant test (distinct from StopAudio's clean
    /// removal): the mix must keep playing for everyone else while this source contributes silence.</summary>
    public void KillAudio()
    {
        var cts = _audioCts; _audioCts = null;
        cts?.Cancel();   // frames just stop — NO StudentAudioStreamStop sent
    }

    // 16 kHz mono 16-bit PCM, 100 ms (1600 samples), sine at hz. Phase-continuous across frames
    // via the absolute sample index (seq*1600 + i) so consecutive frames don't click at the seam.
    static byte[] Tone(int hz, int seq)
    {
        var pcm = new byte[3200];
        double step = 2.0 * Math.PI * hz / 16000.0;
        for (int i = 0; i < 1600; i++)
        {
            short s = (short)(0.2 * 32767 * Math.Sin((seq * 1600L + i) * step));
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }
}


// ───────────────────────────── --classroom N: N replay-clients, one process ─────────────────────────────
static class Classroom
{
    public static async Task<int> RunAsync(string ip, int port, int n, int durationSec, bool audio)
    {
        Console.WriteLine($"=== MockStudent --classroom {n} → {ip}:{port} (1 golden encode, {n} replay-clients, {durationSec}s{(audio ? ", +synthetic mic 0x032C" : "")}) ===");

        var (golden, keyframes) = await GoldenSample.CaptureScreenH264Async();
        if (golden.Count == 0 || keyframes == 0)
        {
            if (!audio)
            {
                Console.WriteLine($"  ❌ golden H.264 capture failed (frames={golden.Count}, keyframes={keyframes}) — is Screen Recording granted?");
                return 1;
            }
            // TT-9-D: audio load doesn't need the screen golden — continue audio-only so a
            // headless CLI (no Screen Recording grant) can still drive the teacher-side mixer.
            Console.WriteLine($"  ⚠ golden H.264 capture failed (no Screen Recording?) — continuing AUDIO-ONLY ({n} synthetic mics)");
            golden = new List<byte[]>();
        }
        long goldenBytes = golden.Sum(p => (long)p.Length);
        Console.WriteLine($"  golden: {golden.Count} H.264 frames · {keyframes} keyframe(s) · {goldenBytes / 1024}KB — ONE encode, replayed ×{n}");
        if (audio) Console.WriteLine($"  TT-9-B: each seat also streams continuous 16 kHz mono PCM (~256 kbps/mic → ~{n * 256 / 1000.0:0.0} Mbps aggregate at {n} mics)");

        var clients = new List<ReplayClient>();
        // distinct tone per seat (220 Hz, +20 Hz each) → the teacher-side mix is separable by ear/FFT.
        for (int i = 0; i < n; i++) clients.Add(new ReplayClient(golden, toneHz: 220 + i * 20, autoAudio: audio));
        using var cts = new CancellationTokenSource();
        var runs = clients.Select((c, i) => c.Wire.RunAsync(ip, port, $"Replay-{i:D2}", cts.Token)).ToList();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastFrames = 0, lastAudio = 0;
        while (sw.Elapsed.TotalSeconds < durationSec)
        {
            await Task.Delay(2000);
            int connected = clients.Count(c => c.Wire.Status == WireStatus.Connected);
            long total = clients.Sum(c => c.FramesSent);
            double fps = (total - lastFrames) / 2.0; lastFrames = total;
            if (audio)
            {
                long au = clients.Sum(c => c.AudioFramesSent);
                double afps = (au - lastAudio) / 2.0; lastAudio = au;
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:0}s] connected={connected}/{n} · screen={total} (~{fps:0} fps) · audio={au} (~{afps:0} frm/s ≈ {afps / 10:0} mic-s worth)");
            }
            else
                Console.WriteLine($"  [{sw.Elapsed.TotalSeconds:0}s] connected={connected}/{n} · frames sent={total} · ~{fps:0} fps aggregate");
        }

        int finalConnected = clients.Count(c => c.Wire.Status == WireStatus.Connected);
        long finalFrames = clients.Sum(c => c.FramesSent);
        long finalAudio = clients.Sum(c => c.AudioFramesSent);
        cts.Cancel();
        try { await Task.WhenAll(runs); } catch { }

        // Judge an --audio run on AUDIO (screen frames only flow if the teacher requests a stream,
        // which a mix host does not); a screen-only run on screen frames (the TT-0 stress meaning).
        bool ok = finalConnected == n && (audio ? finalAudio > 0 : finalFrames > 0);
        var audioNote = audio ? $" · {finalAudio} audio frames (0x032C)" : "";
        Console.WriteLine(ok
            ? $"\n=== CLASSROOM {n} ✅ — {finalConnected}/{n} clients connected · {finalFrames} frames delivered{audioNote} (1 encode → {n} streams) ==="
            : $"\n=== CLASSROOM {n} ❌ — {finalConnected}/{n} connected · {finalFrames} frames{audioNote} ===");
        return ok ? 0 : 1;
    }
}


// ───────────────────────────── TT-9-D: --mixtest N — mixer stall + cap gate ─────────────────────────────
// Self-contained: the REAL TeacherSession (ControlServer + TeacherAudioMixer, production wiring) on
// loopback + N in-process synthetic mic senders (autoAudio ReplayClients — no golden capture, so no
// Screen Recording needed). Asserts the load-bearing NON-BLOCKING invariant — kill one sender
// mid-stream (NO stop message = a network stall) and the mix MUST keep advancing, the survivor MUST
// keep playing, and ONLY the killed source freezes — plus the CAP visible-degradation (N>12 → 12
// mixed, all N counted). Reports whole-process CPU (an upper bound; the senders share this process —
// the clean teacher-only figure comes from --mixhost). SKIPs (exit 0) if the native mix engine can't
// start (no audio output device, e.g. headless CI).
static class MixTest
{
    public static async Task<int> RunAsync(int n, int durationSec)
    {
        if (!ClassroomCtrl.Avalonia.Teacher.Services.TeacherAudioMixer.IsSupported)
        { Console.WriteLine("=== --mixtest SKIP — not macOS ==="); return 0; }
        if (n < 2) { Console.WriteLine("=== --mixtest needs N ≥ 2 (the stall test needs a survivor) ==="); return 1; }

        const int cap = 12;
        Console.WriteLine($"=== MockStudent --mixtest {n} (REAL TeacherSession + {n} synthetic mics; cap {cap}) ===");

        using var session = new ClassroomCtrl.Avalonia.Teacher.Services.TeacherSession(0, IPAddress.Loopback);
        await session.StartAsync();
        int port = session.BoundPort ?? 0;
        int lastMixed = 0, lastOpen = 0;
        session.MixStatusChanged += (m, o, _) => { lastMixed = m; lastOpen = o; };

        var clients = new List<ReplayClient>();
        for (int i = 0; i < n; i++) clients.Add(new ReplayClient(Array.Empty<byte[]>(), toneHz: 200 + i * 13, autoAudio: true));
        using var cts = new CancellationTokenSource();
        var runs = clients.Select((c, i) => c.Wire.RunAsync("127.0.0.1", port, $"Mic-{i:D2}", cts.Token)).ToList();

        await Task.Delay(2500);   // connect + audio ramp (prebuffer + frames flowing)

        // Probe: RenderedFrames advances only if the native engine started (output device present).
        if (session.MixRenderedFrames() == 0)
        {
            Console.WriteLine("=== --mixtest SKIP — native mix engine did not start (no audio output device?) ===");
            cts.Cancel(); try { await Task.WhenAll(runs); } catch { }
            session.Dispose();
            return 0;
        }

        int failures = 0;
        void Check(string label, bool ok) { Console.WriteLine((ok ? "  ✅ " : "  ❌ ") + label); if (!ok) failures++; }

        int expectMixed = Math.Min(n, cap);
        Check($"native mix has {expectMixed} source(s) (min(N,{cap}))", session.MixActiveCount() == expectMixed);
        if (n > cap)
            Check($"CAP: {n} mics open → mixed=={cap} (capped), open=={n} (all counted — none silent-dropped)",
                  lastMixed == cap && lastOpen == n);
        else
            Check($"no cap needed: mixed=={n}, open=={n}", lastMixed == n && lastOpen == n);

        Console.WriteLine($"  ℹ mix output RMS={session.MixOutputRms()} (non-zero ⇒ audible mix)");

        // ── CPU over a short window (whole process — upper bound) ──
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var c0 = proc.TotalProcessorTime; var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(Math.Max(2, durationSec) * 1000);
        proc.Refresh();
        double cores = (proc.TotalProcessorTime - c0).TotalSeconds / Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Console.WriteLine($"  ℹ CPU ~{cores * 100:0}% of one core (WHOLE process: {n} senders + server + mix — teacher-only is far lower; use --mixhost)");

        // ── THE DISTINGUISHING GATE: kill one MIXED sender (network stall — no stop message) ──
        var mixedClients = clients.Where(c => session.MixSourcePlayed(c.Wire.EndpointId) >= 0).ToList();
        if (mixedClients.Count < 2)
            Check("stall test needs ≥2 mixed sources", false);
        else
        {
            var killed = mixedClients[0]; var survivor = mixedClients[1];
            Guid kId = killed.Wire.EndpointId, sId = survivor.Wire.EndpointId;
            killed.KillAudio();                     // frames STOP; source stays registered → node starves
            await Task.Delay(1400);                 // let the killed node's pending (≤~1 s) drain to empty
            long rendered0 = session.MixRenderedFrames(), surv0 = session.MixSourcePlayed(sId), kill0 = session.MixSourcePlayed(kId);
            await Task.Delay(1200);                  // observation window (killed now frozen)
            long rendered1 = session.MixRenderedFrames(), surv1 = session.MixSourcePlayed(sId), kill1 = session.MixSourcePlayed(kId);

            Check($"STALL: the mix keeps rendering for the class (rendered {rendered0}→{rendered1})", rendered1 > rendered0);
            Check($"STALL: the survivor keeps playing (played {surv0}→{surv1})", surv1 > surv0);
            Check($"STALL: the stalled source froze but stayed in the mix (played {kill0}→{kill1}, ≥0 ⇒ not removed)",
                  kill1 == kill0 && kill1 >= 0);
        }

        cts.Cancel(); try { await Task.WhenAll(runs); } catch { }
        session.Dispose();

        Console.WriteLine(failures == 0
            ? $"\n=== MIXTEST {n} PASS ✅ — {expectMixed} mixed · cap enforced · one stalled stream never silenced the class ==="
            : $"\n=== MIXTEST {n} FAIL ❌ ({failures}) ===");
        return failures == 0 ? 0 : 1;
    }
}


// ───────────────────────────── TT-9-D: --mixhost — teacher-only mix CPU host ─────────────────────────────
// Run ONLY the teacher (real TeacherSession = ControlServer + TeacherAudioMixer) on 0.0.0.0:port,
// auto-open every connecting student's mic, and print per-second teacher-PROCESS CPU + mix status.
// The clean N=10/25/50 measurement: point `--classroom N --audio --ip <this-mac>` (or real Mac
// students) at it and read the CPU column — the senders are OTHER processes, so this figure is
// teacher-only. Ctrl+C or --duration to stop.
static class MixHost
{
    public static async Task<int> RunAsync(int port, int durationSec)
    {
        if (!ClassroomCtrl.Avalonia.Teacher.Services.TeacherAudioMixer.IsSupported)
        { Console.WriteLine("=== --mixhost SKIP — not macOS ==="); return 0; }

        using var session = new ClassroomCtrl.Avalonia.Teacher.Services.TeacherSession(port);
        await session.StartAsync();
        Console.WriteLine($"=== MockStudent --mixhost — {session.ListenAddress} — auto-listening to every mic; Ctrl+C to stop ===");

        int mixed = 0, open = 0, cap = 0, students = 0;
        session.MixStatusChanged += (m, o, c) => { mixed = m; open = o; cap = c; };
        session.Roster.StudentAdded += (_, e) =>
        { Interlocked.Increment(ref students); _ = session.ListenToStudentAsync(e.EndpointId, CancellationToken.None); };
        session.Roster.StudentRemoved += (_, __) => Interlocked.Decrement(ref students);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cts.Cancel(); };
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastCpu = proc.TotalProcessorTime; var lastT = sw.Elapsed;
        while (!cts.IsCancellationRequested && (durationSec <= 0 || sw.Elapsed.TotalSeconds < durationSec))
        {
            try { await Task.Delay(1000, cts.Token); } catch { break; }
            proc.Refresh();
            var now = sw.Elapsed; var cpu = proc.TotalProcessorTime;
            double cores = (cpu - lastCpu).TotalSeconds / Math.Max(0.001, (now - lastT).TotalSeconds);
            lastCpu = cpu; lastT = now;
            Console.WriteLine($"  [{now.TotalSeconds:0}s] students={Volatile.Read(ref students)} · mix={mixed}/{open} (cap {cap}) · teacher CPU ~{cores * 100:0}% of one core · RMS={session.MixOutputRms()}");
        }
        session.Dispose();
        Console.WriteLine("=== --mixhost stopped ===");
        return 0;
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

        // (2b) TT-6-D — receive-side target filter: a command targeted at ANOTHER student must be
        // dropped by THIS student (the wrong-blast-radius fix). The student RECEIVES the frame
        // (the Teacher broadcasts + relies on client filtering), but its filter must return false.
        // Assert the negative for BOTH high-blast commands (LockScreen, StudentStreamStart) + the
        // positive (targeted-at-me → acts).
        var recvEnv = new ConcurrentBag<Envelope>();
        client.EnvelopeReceived += env => recvEnv.Add(env);
        var otherId = Guid.NewGuid();
        if (serverStream is not null)
        {
            await Framing.SendAsync(serverStream, Envelope.CreateTargeted(MessageType.LockScreen, Array.Empty<byte>(), teacherId, otherId));
            await Framing.SendAsync(serverStream, Envelope.CreateTargeted(MessageType.StudentStreamStart, Array.Empty<byte>(), teacherId, otherId));
            await Framing.SendAsync(serverStream, Envelope.CreateTargeted(MessageType.LockScreen, Array.Empty<byte>(), teacherId, client.EndpointId));
        }
        Envelope? Rx(MessageType t, Guid target) => recvEnv.FirstOrDefault(e => e.Type == t && e.TargetEndpointId == target);
        Check("filter: LockScreen for ANOTHER student → received but IsForMe FALSE (would NOT lock)",
            await WaitUntil(() => Rx(MessageType.LockScreen, otherId) is not null, 3000)
            && !StudentEnvelopeFilter.IsForMe(Rx(MessageType.LockScreen, otherId)!, client.EndpointId));
        Check("filter: StudentStreamStart for ANOTHER student → received but IsForMe FALSE (no wrong screen capture)",
            await WaitUntil(() => Rx(MessageType.StudentStreamStart, otherId) is not null, 3000)
            && !StudentEnvelopeFilter.IsForMe(Rx(MessageType.StudentStreamStart, otherId)!, client.EndpointId));
        Check("filter: LockScreen for ME → received and IsForMe TRUE (acts)",
            await WaitUntil(() => Rx(MessageType.LockScreen, client.EndpointId) is not null, 3000)
            && StudentEnvelopeFilter.IsForMe(Rx(MessageType.LockScreen, client.EndpointId)!, client.EndpointId));

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

        // ── (0d) TT-6-D RECEIVE-SIDE FILTER (StudentEnvelopeFilter.IsForMe) — the wrong-blast-
        //    radius fix (the port dropped the shipped Service's IsForMe when it collapsed
        //    Service+Agent into one process). DEFAULT-DENY: only whole-class broadcast / my-endpoint
        //    / my-group act. Covers BOTH high-blast-radius commands (Lock, StudentStreamStart).
        Console.WriteLine("-- (0d) TT-6-D receive-side filter (StudentEnvelopeFilter.IsForMe) --");
        {
            var me = Guid.NewGuid(); var other = Guid.NewGuid(); var teacher = Guid.NewGuid();
            Envelope Targeted(MessageType t, Guid target) => Envelope.CreateTargeted(t, Array.Empty<byte>(), teacher, target);

            Check("IsForMe: LockScreen targeted at me → ACT", StudentEnvelopeFilter.IsForMe(Targeted(MessageType.LockScreen, me), me));
            Check("IsForMe: LockScreen targeted at ANOTHER → DROP (no wrong-lock)", !StudentEnvelopeFilter.IsForMe(Targeted(MessageType.LockScreen, other), me));
            Check("IsForMe: StudentStreamStart targeted at me → ACT", StudentEnvelopeFilter.IsForMe(Targeted(MessageType.StudentStreamStart, me), me));
            Check("IsForMe: StudentStreamStart targeted at ANOTHER → DROP (no wrong-screen-capture)", !StudentEnvelopeFilter.IsForMe(Targeted(MessageType.StudentStreamStart, other), me));
            Check("IsForMe: whole-class broadcast (Empty target) → ACT", StudentEnvelopeFilter.IsForMe(Envelope.Create(MessageType.LockScreen, Array.Empty<byte>(), teacher), me));
            Check("IsForMe: group-targeted while in NO room → DROP (default-deny)",
                !StudentEnvelopeFilter.IsForMe(Envelope.CreateGroupTargeted(MessageType.LockScreen, Array.Empty<byte>(), teacher, Guid.NewGuid()), me, myRoomId: null));
            var room = Guid.NewGuid();
            Check("IsForMe: my-group target while in that room → ACT",
                StudentEnvelopeFilter.IsForMe(Envelope.CreateGroupTargeted(MessageType.LockScreen, Array.Empty<byte>(), teacher, room), me, myRoomId: room));
        }

        // ── (0e) TT-7 AGGREGATION / ATTRIBUTION — hand-raise / reaction / chat from 2 students map
        //    to the RIGHT tile, never a fan-out. Asserts the NEGATIVE (B does NOT light when A raises)
        //    — the distinguishing property (a "the target got it" check passes with a broadcast-to-all
        //    bug present). Drives the REAL TeacherGridViewModel / ChatViewModel via their dispatcher-
        //    free Apply* methods (no Avalonia runtime needed) + a recording sound fake (asserts the
        //    HandRaise/Chat SOUND actually fired, not just that a flag flipped).
        Console.WriteLine("-- (0e) TT-7 aggregation: hand-raise / reaction / chat attribution (2 students) --");
        {
            using var aggServer = new ControlServer(NullLogger<ControlServer>.Instance, NullLoggerFactory.Instance,
                IPAddress.Loopback, 0, IPAddress.Loopback, 8000);   // constructed, NOT started (no port bind)
            var grid = new TeacherGridViewModel(new StudentRoster(aggServer));
            var sound = new RecordingSoundService();
            grid.AttachMessaging(new FakeTeacherMessaging(), sound);   // sets sound/notify; events unused (Apply* called directly)

            var idA = Guid.NewGuid(); var idB = Guid.NewGuid();
            var tileA = new StudentTileViewModel(idA, "Alice", "mac-A");
            var tileB = new StudentTileViewModel(idB, "Bob", "mac-B");
            grid.Students.Add(tileA); grid.Students.Add(tileB);

            // A raises → ONLY A's tile lights; B does NOT (the distinguishing negative).
            grid.ApplyHandRaise(new HandRaiseMessage { StudentId = idA, StudentName = "Alice", IsRaised = true });
            Check("hand-raise for A → A's tile raised", tileA.IsHandRaised);
            Check("hand-raise for A → B's tile NOT raised (no wrong-tile fan-out)", !tileB.IsHandRaised);
            Check("hand-raise for A → A queued order 1", tileA.HandRaiseOrder == 1);
            Check("hand-raise for A → RaisedHands == [A]", grid.RaisedHands.Count == 1 && grid.RaisedHands[0] == tileA);
            Check("hand-raise → HandRaise SOUND played (channel, not just a flag)", sound.Played.Contains(NotificationSound.HandRaise));

            // B raises → both up, order preserved (A then B).
            grid.ApplyHandRaise(new HandRaiseMessage { StudentId = idB, StudentName = "Bob", IsRaised = true });
            Check("hand-raise for B → B queued order 2 (ordering preserved)", tileB.HandRaiseOrder == 2);
            Check("hand-raise for B → RaisedHands == [A, B]", grid.RaisedHands.Count == 2 && grid.RaisedHands[0] == tileA && grid.RaisedHands[1] == tileB);

            // reaction for A → only A's tile carries the emoji.
            grid.ApplyReaction(idA, "🎉");
            Check("reaction for A → A's tile shows 🎉", tileA.LastReaction == "🎉");
            Check("reaction for A → B's tile has NO reaction (distinguishing)", string.IsNullOrEmpty(tileB.LastReaction));

            // lower A (the teacher Recognize path) → A cleared, queue collapses to [B].
            grid.ApplyHandRaise(new HandRaiseMessage { StudentId = idA, IsRaised = false });
            Check("hand-lower A → A cleared", !tileA.IsHandRaised && tileA.HandRaiseOrder == 0);
            Check("hand-lower A → RaisedHands == [B]", grid.RaisedHands.Count == 1 && grid.RaisedHands[0] == tileB);

            // an event for an UNKNOWN student is a no-op (never touches A or B).
            grid.ApplyHandRaise(new HandRaiseMessage { StudentId = Guid.NewGuid(), IsRaised = true });
            Check("hand-raise for an unknown id → no known tile touched", !tileA.IsHandRaised && grid.RaisedHands.Count == 1);

            // chat attribution: an incoming line is logged under the SENDER's name + plays the chat sound.
            var chatSound = new RecordingSoundService();
            var chat = new ChatViewModel(new FakeTeacherMessaging(), chatSound, grid);
            chat.ApplyIncomingChat(new ClassroomCtrl.Shared.Protocol.ChatMessage { SenderId = idA, SenderName = "Alice", Text = "can you help?" });
            Check("chat from A → logged as Alice, incoming",
                chat.Messages.Count == 1 && chat.Messages[0].Sender == "Alice" && chat.Messages[0].Text == "can you help?" && !chat.Messages[0].IsOwn);
            Check("incoming chat → Chat SOUND played", chatSound.Played.Contains(NotificationSound.Chat));
        }

        // ── (0f) TT-8 TEACHER SCREEN-SHARE — broadcast reaches EVERY student (unlike a targeted
        //    command) + the extracted ScreenFrameDecoder (Media) dispatch/fallback-signal. Decode
        //    of real pixels is LIVE-proven (needs Skia); here we assert the routing + the signal.
        Console.WriteLine("-- (0f) TT-8 teacher screen-share: broadcast routing + ScreenFrameDecoder signal --");
        {
            var teacher = Guid.NewGuid(); var me = Guid.NewGuid();
            Check("IsForMe: ScreenStreamStart broadcast → ACT (the whole class sees the teacher's screen)",
                StudentEnvelopeFilter.IsForMe(Envelope.Create(MessageType.ScreenStreamStart, Array.Empty<byte>(), teacher), me));
            Check("IsForMe: ScreenStreamStop broadcast → ACT",
                StudentEnvelopeFilter.IsForMe(Envelope.Create(MessageType.ScreenStreamStop, Array.Empty<byte>(), teacher), me));

            // TT-10-B — the filter must NOT eat teacher audio: Start/Stop are broadcast
            // (TargetEndpointId == Empty) → IsForMe ACT. Frames are early-handled BEFORE the
            // filter in the Student's Dispatch (the high-freq bypass, like ScreenStreamFrame) —
            // assert the filter would pass them anyway, so even a bypass regression can't
            // silently kill teacher audio.
            Check("IsForMe: AudioStreamStart broadcast → ACT (bug-#7 reliable control reaches all)",
                StudentEnvelopeFilter.IsForMe(Envelope.Create(MessageType.AudioStreamStart, Array.Empty<byte>(), teacher), me));
            Check("IsForMe: AudioStreamStop broadcast → ACT",
                StudentEnvelopeFilter.IsForMe(Envelope.Create(MessageType.AudioStreamStop, Array.Empty<byte>(), teacher), me));
            Check("IsForMe: AudioStreamFrame broadcast → ACT (frames bypass the filter; belt-and-braces)",
                StudentEnvelopeFilter.IsForMe(Envelope.Create(MessageType.AudioStreamFrame, Array.Empty<byte>(), teacher), me));

            var dec = new ClassroomCtrl.Avalonia.Media.ScreenFrameDecoder();
            var h264Key = new ScreenStreamFrameMessage { FrameData = new byte[] { 0, 0, 0, 1, 0x67, 1, 2, 3 }, Codec = VideoCodec.H264, IsKeyframe = true, Width = 1920, Height = 1080 };
            Check("decoder: undecodable H.264 KEYFRAME → null + LastKeyframeDecodeFailed (fallback signal)",
                dec.Decode(h264Key) is null && dec.LastKeyframeDecodeFailed);
            var h264Delta = new ScreenStreamFrameMessage { FrameData = new byte[] { 0, 0, 0, 1, 0x41, 9 }, Codec = VideoCodec.H264, IsKeyframe = false };
            Check("decoder: H.264 DELTA before a keyframe → null, NOT a failure (waiting)",
                dec.Decode(h264Delta) is null && !dec.LastKeyframeDecodeFailed);
            Check("decoder: empty MJPEG payload → null (no throw)",
                ClassroomCtrl.Avalonia.Media.ScreenFrameDecoder.DecodeMjpeg(Array.Empty<byte>()) is null);
            dec.Dispose();
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

            // (1c) TT-8 — teacher SCREEN-SHARE broadcast reaches the student (Start → Frame → Stop),
            // and the frame payload round-trips intact (dims/codec/seq/bytes). This is the teacher→
            // students path (the inverse of the per-student command path in 1b).
            int shareStart = 0, shareStop = 0, frameRx = 0;
            ScreenStreamFrameMessage? gotFrame = null;
            r.EnvelopeReceived += e =>
            {
                if (e.Type == MessageType.ScreenStreamStart) Interlocked.Increment(ref shareStart);
                else if (e.Type == MessageType.ScreenStreamStop) Interlocked.Increment(ref shareStop);
                else if (e.Type == MessageType.ScreenStreamFrame)
                {
                    gotFrame = MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(e.Payload);
                    Interlocked.Increment(ref frameRx);   // barrier: publishes gotFrame before the poll reads it
                }
            };
            await server.BroadcastScreenStreamControlAsync(true, CancellationToken.None);
            Check("teacher Share START → student receives ScreenStreamStart",
                await WaitUntil(() => Volatile.Read(ref shareStart) > 0, 3000));
            var testFrame = new ScreenStreamFrameMessage
            {
                FrameData = new byte[] { 1, 2, 3, 4, 5 }, Width = 1280, Height = 720,
                Codec = VideoCodec.Mjpeg, IsKeyframe = true, FrameSeq = 7,
            };
            await server.BroadcastScreenFrameAsync(testFrame, CancellationToken.None);
            Check("teacher screen frame → student receives it intact (1280×720, Mjpeg, seq 7, 5 bytes)",
                await WaitUntil(() => Volatile.Read(ref frameRx) > 0, 3000)
                && gotFrame is not null && gotFrame.Width == 1280 && gotFrame.Height == 720
                && gotFrame.Codec == VideoCodec.Mjpeg && gotFrame.FrameSeq == 7 && gotFrame.FrameData.Length == 5);
            await server.BroadcastScreenStreamControlAsync(false, CancellationToken.None);
            Check("teacher Share STOP (reliable, bug #5) → student receives ScreenStreamStop",
                await WaitUntil(() => Volatile.Read(ref shareStop) > 0, 3000));

            // Clean disconnect removes it from the roster.
            rCts.Cancel(); try { await rRun.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
            Check("clean disconnect removes the student from the roster", await WaitUntil(() => !roster.Contains(r.EndpointId), 3000));

            // (2) 3 REAL WireClients → disconnect the MIDDLE one → the RIGHT one is removed.
            var a = StartClient(port, "Alice"); var b = StartClient(port, "Bob"); var c = StartClient(port, "Cara");
            Check("3 real students joined", await WaitUntil(() => roster.Count == 3, 5000));

            // (2b) TT-6-D — WRONG-BLAST-RADIUS with 3 connected: a command targeted at A (Alice) is
            // broadcast to all peers, so bystander C RECEIVES it — but C's filter must DROP it. This
            // is the scenario TT-5 never tested (one student at a time). Assert for BOTH high-blast
            // commands, over the REAL Teacher (CreateTargeted) + real transport.
            var cRx = new System.Collections.Concurrent.ConcurrentBag<Envelope>();
            c.client.EnvelopeReceived += e => cRx.Add(e);
            await server.LockOneAsync(a.client.EndpointId, true, CancellationToken.None, reliable: true);
            await server.RequestStudentStreamAsync(a.client.EndpointId, VideoCodec.Mjpeg, CancellationToken.None);
            Envelope? CRx(MessageType t) => cRx.FirstOrDefault(e => e.Type == t && e.TargetEndpointId == a.client.EndpointId);
            Check("targeted-at-A LockScreen: bystander C DROPS it (IsForMe false for C, true for A)",
                await WaitUntil(() => CRx(MessageType.LockScreen) is not null, 3000)
                && !StudentEnvelopeFilter.IsForMe(CRx(MessageType.LockScreen)!, c.client.EndpointId)
                && StudentEnvelopeFilter.IsForMe(CRx(MessageType.LockScreen)!, a.client.EndpointId));
            Check("targeted-at-A StudentStreamStart: bystander C DROPS it (no wrong screen capture)",
                await WaitUntil(() => CRx(MessageType.StudentStreamStart) is not null, 3000)
                && !StudentEnvelopeFilter.IsForMe(CRx(MessageType.StudentStreamStart)!, c.client.EndpointId));

            // (2c) TT-10-B — teacher TALK broadcast reaches ALL students (the distinguishing
            // property for a BROADCAST — the mirror of TT-6-D's "only the target acts"). With
            // 3 connected: Start (reliable, bug #7) → A AND C both receive; a 3200-byte PCM
            // frame (lossy-class audio channel) → both receive it intact; Stop (reliable) →
            // both receive. One receiver would pass with a targeted-send bug present; TWO
            // independent receivers prove the fan-out.
            int aAudStart = 0, aAudStop = 0, aAudFrame = 0, cAudStart = 0, cAudStop = 0, cAudFrame = 0;
            AudioStreamFrameMessage? aGot = null, cGot = null;
            a.client.EnvelopeReceived += e =>
            {
                if (e.Type == MessageType.AudioStreamStart) Interlocked.Increment(ref aAudStart);
                else if (e.Type == MessageType.AudioStreamStop) Interlocked.Increment(ref aAudStop);
                else if (e.Type == MessageType.AudioStreamFrame)
                { aGot = MessagePackSerializer.Deserialize<AudioStreamFrameMessage>(e.Payload); Interlocked.Increment(ref aAudFrame); }
            };
            c.client.EnvelopeReceived += e =>
            {
                if (e.Type == MessageType.AudioStreamStart) Interlocked.Increment(ref cAudStart);
                else if (e.Type == MessageType.AudioStreamStop) Interlocked.Increment(ref cAudStop);
                else if (e.Type == MessageType.AudioStreamFrame)
                { cGot = MessagePackSerializer.Deserialize<AudioStreamFrameMessage>(e.Payload); Interlocked.Increment(ref cAudFrame); }
            };
            await server.BroadcastAudioStreamControlAsync(true, CancellationToken.None);
            Check("Talk START (reliable, bug #7) → BOTH A and C receive AudioStreamStart",
                await WaitUntil(() => Volatile.Read(ref aAudStart) > 0 && Volatile.Read(ref cAudStart) > 0, 3000));
            var pcm = new byte[3200]; for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)(i & 0xFF);
            await server.BroadcastAudioFrameAsync(new AudioStreamFrameMessage
            { PcmData = pcm, SampleRate = 16000, Channels = 1, BitsPerSample = 16, FrameSeq = 42 }, CancellationToken.None);
            Check("Talk frame (lossy-class audio channel) → BOTH receive it intact (3200 B, 16 kHz mono, seq 42)",
                await WaitUntil(() => Volatile.Read(ref aAudFrame) > 0 && Volatile.Read(ref cAudFrame) > 0, 3000)
                && aGot is { FrameSeq: 42, SampleRate: 16000, Channels: 1 } && aGot.PcmData.Length == 3200
                && cGot is { FrameSeq: 42, SampleRate: 16000, Channels: 1 } && cGot.PcmData.Length == 3200);
            await server.BroadcastAudioStreamControlAsync(false, CancellationToken.None);
            Check("Talk STOP (reliable, bug #7 — no stuck playback session) → BOTH receive AudioStreamStop",
                await WaitUntil(() => Volatile.Read(ref aAudStop) > 0 && Volatile.Read(ref cAudStop) > 0, 3000));

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
            ? "\n=== MOCKSTUDENT TEACHERSELFTEST PASS ✅ — command wiring (reliable channel) + TT-7 chat/hand/reaction attribution + TT-8 screen-share broadcast + real-client interop + roster fix + command delivery + ownership guard + stale-sweep + guaranteed teardown ==="
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

// TT-7-E — a no-op ITeacherMessaging. Its events are never raised: the aggregation gate calls the
// grid/chat Apply* methods directly, so no Avalonia dispatcher is needed. AttachMessaging still
// wires the (unused) events; passing this satisfies the seam.
#pragma warning disable CS0067   // events intentionally unused (gate drives Apply* directly)
sealed class FakeTeacherMessaging : ITeacherMessaging
{
    public event EventHandler<ClassroomCtrl.Shared.Protocol.ChatMessage>? ChatReceived;
    public event EventHandler<HandRaiseMessage>? HandRaiseReceived;
    public event EventHandler<(Guid SenderId, ReactionMessage Msg)>? ReactionReceived;
    public Task BroadcastChatAsync(string text, CancellationToken ct) => Task.CompletedTask;
    public Task SendDirectMessageAsync(Guid endpointId, string text, CancellationToken ct) => Task.CompletedTask;
    public Task BroadcastReactionAsync(ReactionMessage msg, CancellationToken ct) => Task.CompletedTask;
    public Task SendHandLowerAsync(Guid studentId, CancellationToken ct) => Task.CompletedTask;
}
#pragma warning restore CS0067

// TT-7-E — records which notification sounds fired, so the gate asserts the CHANNEL (a hand-raise
// actually plays a HandRaise sound), not merely that a tile flag flipped.
sealed class RecordingSoundService : ISoundService
{
    public List<NotificationSound> Played { get; } = new();
    public void Play(NotificationSound sound) => Played.Add(sound);
}

// ───────────────────────────── TT-12: --filetest — file-transfer E2E gate ─────────────────────────────
// The permanent Student-track gate for "teacher sends a file to the class". Spins the REAL ControlServer
// on loopback, connects TWO REAL WireClients (the same transport the Mac Student ships), and feeds each
// client's frames through the REAL StudentEnvelopeFilter.IsForMe + the REAL FileReceiver — i.e. the exact
// code path the app runs. Asserts the DISTINGUISHING properties, not the happy path:
//   • broadcast → BOTH A and B save a BYTE-IDENTICAL file (the SAVED file's SHA-256 == the source's),
//   • targeted→A → A saves it AND B receives NOTHING (A-only ↛ B), the per-student negative that a
//     "the target got it" check would miss (it rides the same IsForMe filter as every command — TT-6-D).
// The reliable channel is exercised implicitly (BroadcastFileAsync uses BroadcastReliableAsync — a lossy
// path would truncate the 250 KB / 4-chunk transfer and the SHA check would fail).
static class FileTest
{
    public static async Task<int> RunAsync()
    {
        int failures = 0;
        void Check(string label, bool ok)
        {
            if (ok) Console.WriteLine($"  ✅ {label}");
            else { Console.WriteLine($"  ❌ {label}"); failures++; }
        }

        Console.WriteLine("=== MockStudent --filetest (REAL ControlServer + REAL WireClient + REAL FileReceiver) ===");

        var server = new ControlServer(NullLogger<ControlServer>.Instance, NullLoggerFactory.Instance,
            IPAddress.Loopback, 0, IPAddress.Loopback, 30000);   // long stale window: file transfer takes a moment
        var roster = new StudentRoster(server);
        var tmpRoot = Path.Combine(Path.GetTempPath(), "ntyfiletest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpRoot);

        try
        {
            await server.StartAsync(CancellationToken.None);
            int port = server.BoundPort ?? -1;
            Check("server bound an ephemeral port", port > 0);

            // Two REAL student clients.
            var aClient = new WireClient(); var aCts = new CancellationTokenSource();
            var aRun = aClient.RunAsync("127.0.0.1", port, "Alice", aCts.Token);
            var bClient = new WireClient(); var bCts = new CancellationTokenSource();
            var bRun = bClient.RunAsync("127.0.0.1", port, "Bob", bCts.Token);
            Check("2 students joined the roster", await WaitUntil(() => roster.Count == 2, 5000));

            // Real receivers into isolated temp dirs; count completions with volatile ints.
            var aRecv = new FileReceiver(Path.Combine(tmpRoot, "A"));
            var bRecv = new FileReceiver(Path.Combine(tmpRoot, "B"));
            var aResults = new List<FileReceiveResult>(); var bResults = new List<FileReceiveResult>();
            int aDone = 0, bDone = 0;
            aRecv.FileReceived += r => { lock (aResults) aResults.Add(r); Interlocked.Increment(ref aDone); };
            bRecv.FileReceived += r => { lock (bResults) bResults.Add(r); Interlocked.Increment(ref bDone); };

            void Feed(FileReceiver recv, Guid myId, Envelope env)
            {
                if (!StudentEnvelopeFilter.IsForMe(env, myId)) return;   // the REAL client-side target filter
                switch (env.Type)
                {
                    case MessageType.FileAnnounce: recv.OnAnnounce(MessagePackSerializer.Deserialize<FileAnnounceMessage>(env.Payload)); break;
                    case MessageType.FileChunk:    recv.OnChunk(MessagePackSerializer.Deserialize<FileChunkMessage>(env.Payload)); break;
                    case MessageType.FileComplete: recv.OnComplete(MessagePackSerializer.Deserialize<FileCompleteMessage>(env.Payload)); break;
                }
            }
            var aEnd = aClient.EndpointId; var bEnd = bClient.EndpointId;
            aClient.EnvelopeReceived += e => Feed(aRecv, aEnd, e);
            bClient.EnvelopeReceived += e => Feed(bRecv, bEnd, e);

            // A multi-chunk source file (250 KB → 4× 64 KB chunks) — exercises reassembly + ordering.
            var src = Path.Combine(tmpRoot, "lesson-handout.bin");
            var payload = new byte[250 * 1024];
            new Random(12345).NextBytes(payload);
            File.WriteAllBytes(src, payload);
            string srcSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload));

            bool SavedMatches(FileReceiveResult r) =>
                r.Ok && r.SavedPath != null && File.Exists(r.SavedPath)
                && new FileInfo(r.SavedPath).Length == payload.Length
                && Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(r.SavedPath))) == srcSha;

            // ── Test 1: whole-class broadcast → BOTH receive byte-identical ──
            await server.BroadcastFileAsync(src, CancellationToken.None, targetEndpointId: null);
            Check("broadcast → BOTH A and B complete the transfer",
                await WaitUntil(() => Volatile.Read(ref aDone) > 0 && Volatile.Read(ref bDone) > 0, 8000));
            FileReceiveResult aLast, bLast;
            lock (aResults) aLast = aResults[^1];
            lock (bResults) bLast = bResults[^1];
            Check("A saved a BYTE-IDENTICAL file (saved SHA-256 == source, 250 KB)", SavedMatches(aLast));
            Check("B saved a BYTE-IDENTICAL file (saved SHA-256 == source, 250 KB)", SavedMatches(bLast));

            // ── Test 2: targeted to A only → A gets it, B gets NOTHING (the distinguishing negative) ──
            lock (aResults) aResults.Clear();
            lock (bResults) bResults.Clear();
            Interlocked.Exchange(ref aDone, 0); Interlocked.Exchange(ref bDone, 0);
            var src2 = Path.Combine(tmpRoot, "for-alice-only.bin");
            var payload2 = new byte[130 * 1024]; new Random(999).NextBytes(payload2);
            File.WriteAllBytes(src2, payload2);

            await server.BroadcastFileAsync(src2, CancellationToken.None, targetEndpointId: aEnd);
            Check("targeted→A: A completes the transfer", await WaitUntil(() => Volatile.Read(ref aDone) > 0, 8000));
            lock (aResults) Check("targeted→A: A's copy verified OK", aResults.Count > 0 && aResults[^1].Ok);
            // Generous window for B to (wrongly) receive anything, THEN assert it did not.
            await Task.Delay(1500);
            Check("🔴 targeted→A: B received NOTHING (A-only ↛ B — the distinguishing negative)",
                Volatile.Read(ref bDone) == 0);

            aCts.Cancel(); bCts.Cancel();
            try { await Task.WhenAll(aRun, bRun).WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        catch (Exception ex) { Check($"scenario threw — {ex.GetType().Name}: {ex.Message}", false); }
        finally
        {
            server.Dispose();
            try { Directory.Delete(tmpRoot, recursive: true); } catch { }
        }

        Console.WriteLine(failures == 0
            ? "\n=== MOCKSTUDENT FILETEST PASS ✅ — teacher→class file transfer: broadcast reaches BOTH byte-identical (SHA-256), targeted reaches ONLY the target (A-only ↛ B), reliable channel, real client + real IsForMe + real FileReceiver ==="
            : $"\n=== MOCKSTUDENT FILETEST FAIL ❌ ({failures} check(s)) ===");
        return failures == 0 ? 0 : 1;
    }

    static async Task<bool> WaitUntil(Func<bool> cond, int ms)
    {
        for (int t = 0; t < ms && !cond(); t += 25) await Task.Delay(25);
        return cond();
    }
}
