// Phase 26.0-B — MockTeacher
// ─────────────────────────────────────────────────────────────────────────
// A minimal stand-in for the Windows Teacher's TCP control server, so the Mac
// Sandbox's WireClient can be exercised end-to-end WITHOUT a Windows box:
//   • accepts a TCP connection (framing = [4B BE len][MessagePack(Envelope)]),
//   • logs the student's Hello,
//   • answers every Ping with a Pong (mirrors the shipped TcpControlServer),
//   • pushes Teacher→Student commands on demand (lock / unlock / policy / chat …).
//
// Modes:
//   (default) interactive server — run alongside the Sandbox GUI; type commands
//             at the prompt to push envelopes to the connected student.
//   --selftest — spins up the server AND a real WireClient in-process, drives the
//             full loop (connect → Hello → Ping/Pong → LockScreen → PolicyApply →
//             Chat), asserts each is received, and exits 0 (PASS) / 1 (FAIL).
//
// Usage:
//   dotnet run --project tools/MockTeacher                 # interactive, port 7777
//   dotnet run --project tools/MockTeacher -- --port 9999  # interactive, custom port
//   dotnet run --project tools/MockTeacher -- --selftest   # automated loop proof

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;

int port = 7777;
bool selfTest = false, streamTest = false, streamTestH264 = false, cameraTest = false, audioTest = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--selftest": selfTest = true; break;
        case "--streamtest": streamTest = true; break;
        case "--streamtest-h264": streamTestH264 = true; break;
        case "--cameratest": cameraTest = true; break;
        case "--audiotest": audioTest = true; break;
    }
}

var teacherId = Guid.NewGuid();

if (audioTest) return await AudioTest.RunAsync(teacherId);
if (cameraTest) return await CameraTest.RunAsync(teacherId);
if (streamTestH264) return await StreamTest.RunAsync(teacherId, VideoCodec.H264);
if (streamTest) return await StreamTest.RunAsync(teacherId, VideoCodec.Mjpeg);
return selfTest ? await SelfTest.RunAsync(port == 7777 ? 0 : port, teacherId)
                : await Server.RunInteractiveAsync(port, teacherId);


// ───────────────────────────── shared framing ─────────────────────────────
static class Frame
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

    public static byte[] LockPayload() => Array.Empty<byte>();

    public static byte[] StreamStartPayload(VideoCodec codec = VideoCodec.Mjpeg) =>
        MessagePackSerializer.Serialize(new StudentStreamStartRequest { Codec = codec });

    public static byte[] ConferenceStartPayload(Guid sessionId) =>
        MessagePackSerializer.Serialize(new ConferenceStartMessage
        {
            SessionId = sessionId,
            HostName = "MockTeacher (mock)",
            StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

    public static byte[] PolicyPayload() => MessagePackSerializer.Serialize(new PolicyApplyMessage
    {
        BlockUsbStorage = true,
        BlockPrinting = true,
        BlockedProcessNames = new() { "chrome.exe", "game.exe" },
        BlockedHostnames = new() { "facebook.com", "tiktok.com" },
        ExpiresAtUtcMs = 0,
    });

    public static byte[] ChatPayload(Guid teacherId, string text) => MessagePackSerializer.Serialize(new ChatMessage
    {
        SenderId = teacherId,
        SenderName = "Teacher (mock)",
        Text = text,
        TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
}


// ───────────────────────────── interactive server ─────────────────────────────
static class Server
{
    static int _streamFrames;

    public static async Task<int> RunInteractiveAsync(int port, Guid teacherId)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"=== MockTeacher listening on TCP {port} (teacher {teacherId.ToString()[..8]}) ===");
        Console.WriteLine("Commands once a student connects: lock | unlock | policy | revert | chat <text> | shot | viewscreen | stopscreen | quit");
        Console.WriteLine("Waiting for a student to connect …");

        NetworkStream? live = null;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                var client = await listener.AcceptTcpClientAsync();
                var ep = client.Client.RemoteEndPoint;
                Console.WriteLine($"\n[+] Student connected from {ep}");
                var stream = client.GetStream();
                live = stream;
                _ = Task.Run(() => PumpAsync(stream, teacherId, ep));
            }
        });

        // stdin command loop
        while (true)
        {
            var line = Console.ReadLine();
            if (line == null) break;
            line = line.Trim();
            if (line.Length == 0) continue;
            if (line == "quit") break;
            if (live == null) { Console.WriteLine("(no student connected yet)"); continue; }

            try
            {
                if (line == "lock") { await Frame.SendAsync(live, MessageType.LockScreen, Frame.LockPayload(), teacherId); Console.WriteLine("→ LockScreen"); }
                else if (line == "unlock") { await Frame.SendAsync(live, MessageType.UnlockScreen, Frame.LockPayload(), teacherId); Console.WriteLine("→ UnlockScreen"); }
                else if (line == "policy") { await Frame.SendAsync(live, MessageType.PolicyApply, Frame.PolicyPayload(), teacherId); Console.WriteLine("→ PolicyApply"); }
                else if (line == "revert") { await Frame.SendAsync(live, MessageType.PolicyRevert, Array.Empty<byte>(), teacherId); Console.WriteLine("→ PolicyRevert"); }
                else if (line == "shot") { await Frame.SendAsync(live, MessageType.RequestScreenshot, Array.Empty<byte>(), teacherId); Console.WriteLine("→ RequestScreenshot"); }
                else if (line.StartsWith("chat ")) { await Frame.SendAsync(live, MessageType.ChatBroadcast, Frame.ChatPayload(teacherId, line[5..]), teacherId); Console.WriteLine("→ ChatBroadcast"); }
                else if (line == "viewscreen") { await Frame.SendAsync(live, MessageType.StudentStreamStart, Frame.StreamStartPayload(VideoCodec.Mjpeg), teacherId); Console.WriteLine("→ StudentStreamStart (MJPEG); incoming frames saved to mockteacher-frames/"); }
                else if (line == "viewscreen-h264") { await Frame.SendAsync(live, MessageType.StudentStreamStart, Frame.StreamStartPayload(VideoCodec.H264), teacherId); Console.WriteLine("→ StudentStreamStart (H.264)"); }
                else if (line == "stopscreen") { await Frame.SendAsync(live, MessageType.StudentStreamStop, Array.Empty<byte>(), teacherId); Console.WriteLine("→ StudentStreamStop"); }
                else Console.WriteLine("(unknown command)");
            }
            catch (Exception ex) { Console.WriteLine($"send failed: {ex.Message}"); }
        }

        Console.WriteLine("MockTeacher stopped.");
        return 0;
    }

    static async Task PumpAsync(NetworkStream stream, Guid teacherId, EndPoint? ep)
    {
        try
        {
            while (true)
            {
                var env = await Frame.ReadAsync(stream);
                if (env == null) { Console.WriteLine($"[-] Student {ep} disconnected."); return; }

                if (env.Type == MessageType.Hello)
                {
                    var hello = MessagePackSerializer.Deserialize<HelloMessage>(env.Payload);
                    Console.WriteLine($"[Hello] {hello.DisplayName} · {hello.MachineName} · {hello.OsVersion} · proto={hello.ProtocolVersion} · ep={hello.EndpointId.ToString()[..8]}");
                }
                else if (env.Type == MessageType.Ping)
                {
                    await Frame.SendAsync(stream, MessageType.Pong, Array.Empty<byte>(), teacherId);
                    Console.WriteLine("[Ping] → Pong");
                }
                else if (env.Type == MessageType.StudentStreamFrame)
                {
                    var f = MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(env.Payload);
                    bool jpeg = f.FrameData.Length > 3 && f.FrameData[0] == 0xFF && f.FrameData[1] == 0xD8;
                    _streamFrames++;
                    if (_streamFrames <= 3 || _streamFrames % 10 == 0)
                        Console.WriteLine($"[StreamFrame] seq={f.FrameSeq} codec={f.Codec} {f.Width}x{f.Height} {f.FrameData.Length}B jpeg={jpeg}");
                    // Save the first frame so it can be eyeballed.
                    if (_streamFrames == 1 && jpeg)
                    {
                        Directory.CreateDirectory("mockteacher-frames");
                        File.WriteAllBytes("mockteacher-frames/frame-001.jpg", f.FrameData);
                        Console.WriteLine("  saved mockteacher-frames/frame-001.jpg");
                    }
                }
                else
                {
                    Console.WriteLine($"[RX] {env.Type} ({env.Payload.Length} B)");
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[!] pump error: {ex.Message}"); }
    }
}


// ───────────────────────────── automated self-test ─────────────────────────────
static class SelfTest
{
    public static async Task<int> RunAsync(int fixedPort, Guid teacherId)
    {
        // Bind an ephemeral port (or the fixed one) on loopback.
        var listener = new TcpListener(IPAddress.Loopback, fixedPort);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"=== MockTeacher --selftest (loopback:{port}) ===");

        var helloSeen = new TaskCompletionSource<HelloMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pongSent = new TaskCompletionSource();

        // Server side.
        var serverTask = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            // Read until we've seen Hello + at least one Ping (→ Pong), then push commands.
            bool pushed = false;
            while (true)
            {
                var env = await Frame.ReadAsync(stream);
                if (env == null) return;
                if (env.Type == MessageType.Hello)
                    helloSeen.TrySetResult(MessagePackSerializer.Deserialize<HelloMessage>(env.Payload));
                else if (env.Type == MessageType.Ping)
                {
                    await Frame.SendAsync(stream, MessageType.Pong, Array.Empty<byte>(), teacherId);
                    pongSent.TrySetResult();
                }

                // Once Hello is in, push the three commands the client must receive.
                if (!pushed && helloSeen.Task.IsCompleted)
                {
                    pushed = true;
                    await Frame.SendAsync(stream, MessageType.LockScreen, Frame.LockPayload(), teacherId);
                    await Frame.SendAsync(stream, MessageType.PolicyApply, Frame.PolicyPayload(), teacherId);
                    await Frame.SendAsync(stream, MessageType.ChatBroadcast, Frame.ChatPayload(teacherId, "hello from mock"), teacherId);
                }
            }
        });

        // Client side — the REAL WireClient from the Sandbox assembly.
        var client = new WireClient();
        var received = new System.Collections.Concurrent.ConcurrentBag<MessageType>();
        var lockSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var policySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var chatSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EnvelopeReceived += env =>
        {
            received.Add(env.Type);
            Console.WriteLine($"  [Rx] {env.Type} ({env.Payload.Length} B payload)");
            if (env.Type == MessageType.LockScreen) lockSeen.TrySetResult();
            if (env.Type == MessageType.PolicyApply) policySeen.TrySetResult();
            if (env.Type == MessageType.ChatBroadcast) chatSeen.TrySetResult();
        };
        client.Traffic += (dir, label, size) => Console.WriteLine($"  [{dir}] {label} {(size > 0 ? size + " B" : "")}");

        using var cts = new CancellationTokenSource();
        var run = client.RunAsync("127.0.0.1", port, "SelfTest Student", cts.Token);

        int failures = 0;
        try
        {
            await Check("Hello received by server", helloSeen.Task);

            // The heartbeat Ping is every 5 s; nudge one immediately so the Pong check is fast.
            for (int i = 0; i < 50 && client.Status != WireStatus.Connected; i++) await Task.Delay(100);
            try { await client.SendAsync(MessageType.Ping, Array.Empty<byte>(), CancellationToken.None); } catch { }
            await Check("Pong sent by server in reply to Ping", pongSent.Task);

            await Check("Client received LockScreen", lockSeen.Task);
            await Check("Client received PolicyApply", policySeen.Task);
            await Check("Client received ChatBroadcast", chatSeen.Task);
        }
        catch (TimeoutException ex) { Console.WriteLine($"  ❌ {ex.Message}"); failures++; }

        cts.Cancel();
        try { await run; } catch { }
        listener.Stop();

        Console.WriteLine(failures == 0
            ? "\n=== SELFTEST PASS ✅ — full loop proven locally (connect→Hello→Ping/Pong→receive) ==="
            : $"\n=== SELFTEST FAIL ❌ ({failures} step(s)) ===");
        return failures == 0 ? 0 : 1;
    }

    static async Task Check(string name, Task done, int timeoutMs = 8000)
    {
        var t = await Task.WhenAny(done, Task.Delay(timeoutMs));
        if (t != done) throw new TimeoutException($"timed out: {name}");
        Console.WriteLine($"  ✅ {name}");
    }
}


// ───────────────────────────── screen-stream self-test ─────────────────────────────
// Drives the REAL WireClient + ScreenStreamer (from the Sandbox assembly) against an
// in-process mock Teacher: server sends StudentStreamStart → the Mac captures its screen
// as JPEG → sends StudentStreamFrame → server decodes + validates. Proves the whole
// Mac-side send path emits decodable frames, no Windows box needed.
static class StreamTest
{
    public static async Task<int> RunAsync(Guid teacherId, VideoCodec codec)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"=== MockTeacher --streamtest ({codec}, loopback:{port}) ===");

        int received = 0, valid = 0; long totalBytes = 0;
        int firstFrameKeyframe = -1; bool allAnnexB = true, keyframesHaveParamSets = true, deltasAreSlices = true;
        var gotFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            while (true)
            {
                var env = await Frame.ReadAsync(stream);
                if (env == null) return;
                if (env.Type == MessageType.Hello)
                    await Frame.SendAsync(stream, MessageType.StudentStreamStart, Frame.StreamStartPayload(codec), teacherId);
                else if (env.Type == MessageType.Ping)
                    await Frame.SendAsync(stream, MessageType.Pong, Array.Empty<byte>(), teacherId);
                else if (env.Type == MessageType.StudentStreamFrame)
                {
                    var f = MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(env.Payload);
                    received++;
                    totalBytes += f.FrameData.Length;
                    var data = f.FrameData;

                    if (codec == VideoCodec.H264)
                    {
                        if (firstFrameKeyframe < 0) firstFrameKeyframe = f.IsKeyframe ? 1 : 0;
                        bool annexB = data.Length > 4 && data[0] == 0 && data[1] == 0 && data[2] == 0 && data[3] == 1;
                        if (!annexB) allAnnexB = false;
                        var types = NalTypes(data);
                        if (f.IsKeyframe) { if (!(types.Contains(7) && types.Contains(8) && types.Contains(5))) keyframesHaveParamSets = false; }
                        else { if (!types.Contains(1)) deltasAreSlices = false; }
                        bool structOk = annexB && (f.IsKeyframe ? types.Contains(7) && types.Contains(8) && types.Contains(5) : types.Contains(1));
                        if (structOk) valid++;
                        if (received == 1) { Directory.CreateDirectory("mockteacher-frames"); File.WriteAllBytes("mockteacher-frames/streamtest.h264", data); }
                        if (received <= 3 || f.IsKeyframe)
                            Console.WriteLine($"  [Frame] seq={f.FrameSeq} codec={f.Codec} key={f.IsKeyframe} {f.Width}x{f.Height} {data.Length}B nal=[{string.Join(",", types)}]");
                    }
                    else
                    {
                        bool jpeg = data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[^2] == 0xFF && data[^1] == 0xD9;
                        if (jpeg) valid++;
                        if (received == 1) { Directory.CreateDirectory("mockteacher-frames"); File.WriteAllBytes("mockteacher-frames/streamtest-001.jpg", data); }
                        Console.WriteLine($"  [Frame] seq={f.FrameSeq} codec={f.Codec} {f.Width}x{f.Height} {data.Length}B jpeg={jpeg}");
                    }
                    if (received >= 12) gotFrames.TrySetResult();
                }
            }
        });

        // Mac student: real WireClient + ScreenStreamer; decode requested codec (as the
        // ConnectionViewModel does) and start the matching path.
        var wire = new WireClient();
        var streamer = new ScreenStreamer();
        wire.EnvelopeReceived += env =>
        {
            if (env.Type == MessageType.StudentStreamStart)
            {
                var reqCodec = VideoCodec.Mjpeg;
                try { if (env.Payload.Length > 0) reqCodec = MessagePackSerializer.Deserialize<StudentStreamStartRequest>(env.Payload).Codec; } catch { }
                _ = streamer.StartAsync(wire, reqCodec);
            }
            else if (env.Type == MessageType.StudentStreamStop) _ = streamer.StopAsync();
        };
        using var cts = new CancellationTokenSource();
        var run = wire.RunAsync("127.0.0.1", port, "StreamTest Mac", cts.Token);

        int failures = 0;
        var done = await Task.WhenAny(gotFrames.Task, Task.Delay(20000));
        if (done != gotFrames.Task) { Console.WriteLine("  ❌ timed out waiting for 12 frames"); failures++; }

        await streamer.StopAsync();
        cts.Cancel();
        try { await run; } catch { }
        listener.Stop();

        double bitrate = totalBytes * 8.0 / 4.0 / 1_000_000; // rough: frames span ~a few seconds
        Console.WriteLine($"\n  frames received: {received} · well-formed: {valid}/{received} · avg {(received > 0 ? totalBytes / received : 0)} B/frame");
        bool ok;
        if (codec == VideoCodec.H264)
        {
            Console.WriteLine($"  first frame keyframe: {firstFrameKeyframe == 1} · all Annex-B: {allAnnexB} · "
                            + $"keyframes have SPS+PPS+IDR: {keyframesHaveParamSets} · deltas are slices: {deltasAreSlices}");
            Console.WriteLine($"  ~bitrate: {bitrate:0.00} Mbit/s @ 1920x1080 (MJPEG was ~6.5 Mbit/s @ 1107x720) · sample → mockteacher-frames/streamtest.h264");
            ok = failures == 0 && received >= 12 && valid == received && firstFrameKeyframe == 1
                 && allAnnexB && keyframesHaveParamSets && deltasAreSlices;
        }
        else
        {
            Console.WriteLine($"  valid JPEG (FFD8..FFD9): {valid}/{received} · sample → mockteacher-frames/streamtest-001.jpg");
            ok = failures == 0 && received >= 12 && valid == received;
        }
        Console.WriteLine(ok
            ? $"\n=== STREAMTEST PASS ✅ — Mac emits well-formed {codec} StudentStreamFrames ==="
            : "\n=== STREAMTEST FAIL ❌ ===");
        return ok ? 0 : 1;
    }

    /// <summary>Annex-B NAL unit types present (byte after each 00 00 00 01 start code, low 5 bits).</summary>
    static System.Collections.Generic.List<int> NalTypes(byte[] b)
    {
        var t = new System.Collections.Generic.List<int>();
        for (int i = 0; i + 4 < b.Length; i++)
            if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1) { t.Add(b[i + 4] & 0x1F); i += 4; }
        return t;
    }
}


// ───────────────────────────── camera peer-cam self-test ─────────────────────────────
// Drives the REAL WireClient + CameraStreamer (from the Sandbox assembly) against an
// in-process mock Teacher acting as a Conference host: server sends ConferenceStart →
// the Mac starts its camera → sends ConferenceCameraStart + ConferenceCameraFrame(JPEG)
// → server validates. After ≥12 frames the server sends ConferenceEnd and confirms the
// Mac replies with ConferenceCameraStop (clean stop). Proves the whole 28-B→E chain
// emits decodable peer-cam frames, no Windows box needed.
//
// NOTE: this captures the PHYSICAL camera, so the running process needs Camera
// permission (macOS TCC). The .app bundle grant (28-C) is bound to the bundle id, not
// the `dotnet` CLI host — so a bare `dotnet run` may need its own one-time grant.
static class CameraTest
{
    public static async Task<int> RunAsync(Guid teacherId)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var sessionId = Guid.NewGuid();
        Console.WriteLine($"=== MockTeacher --cameratest (loopback:{port}, session {sessionId.ToString()[..8]}) ===");

        int received = 0, valid = 0, endpointOk = 0; long totalBytes = 0;
        Guid helloEndpoint = Guid.Empty, startEndpoint = Guid.Empty;
        bool gotStart = false, gotStop = false;
        var gotFrames = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var endRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            while (true)
            {
                var env = await Frame.ReadAsync(stream);
                if (env == null) return;
                switch (env.Type)
                {
                    case MessageType.Hello:
                        helloEndpoint = MessagePackSerializer.Deserialize<HelloMessage>(env.Payload).EndpointId;
                        Console.WriteLine($"  [Hello] ep={helloEndpoint.ToString()[..8]} → sending ConferenceStart");
                        await Frame.SendAsync(stream, MessageType.ConferenceStart, Frame.ConferenceStartPayload(sessionId), teacherId);
                        break;
                    case MessageType.Ping:
                        await Frame.SendAsync(stream, MessageType.Pong, Array.Empty<byte>(), teacherId);
                        break;
                    case MessageType.ConferenceCameraStart:
                    {
                        var s = MessagePackSerializer.Deserialize<ConferenceCameraStartMessage>(env.Payload);
                        gotStart = true; startEndpoint = s.SourceEndpointId;
                        Console.WriteLine($"  [ConferenceCameraStart] src={s.SourceEndpointId.ToString()[..8]} name='{s.SourceName}' {s.Width}x{s.Height}@{s.Fps} session={s.SessionId.ToString()[..8]}");
                        break;
                    }
                    case MessageType.ConferenceCameraFrame:
                    {
                        var f = MessagePackSerializer.Deserialize<ConferenceCameraFrameMessage>(env.Payload);
                        received++;
                        totalBytes += f.JpegData.Length;
                        bool jpeg = f.JpegData.Length > 3 && f.JpegData[0] == 0xFF && f.JpegData[1] == 0xD8
                                    && f.JpegData[^2] == 0xFF && f.JpegData[^1] == 0xD9;
                        if (jpeg) valid++;
                        // SourceEndpointId must equal the sender (== Hello EndpointId == Envelope.SenderId).
                        if (f.SourceEndpointId == helloEndpoint && env.SenderId == helloEndpoint) endpointOk++;
                        if (received == 1) { Directory.CreateDirectory("mockteacher-frames"); File.WriteAllBytes("mockteacher-frames/cameratest-001.jpg", f.JpegData); }
                        if (received <= 3 || received % 10 == 0)
                            Console.WriteLine($"  [CamFrame] #{received} src={f.SourceEndpointId.ToString()[..8]} {f.JpegData.Length}B jpeg={jpeg} ts={f.TimestampMs}");
                        if (received >= 12) gotFrames.TrySetResult();
                        break;
                    }
                    case MessageType.ConferenceCameraStop:
                    {
                        var s = MessagePackSerializer.Deserialize<ConferenceCameraStopMessage>(env.Payload);
                        gotStop = true;
                        Console.WriteLine($"  [ConferenceCameraStop] src={s.SourceEndpointId.ToString()[..8]}");
                        stopSeen.TrySetResult();
                        break;
                    }
                }

                // Drive the shutdown once we have enough frames: send ConferenceEnd and
                // expect the Mac to reply with ConferenceCameraStop.
                if (received >= 12 && !endRequested.Task.IsCompleted)
                {
                    endRequested.TrySetResult();
                    Console.WriteLine("  → 12 frames reached; sending ConferenceEnd");
                    await Frame.SendAsync(stream, MessageType.ConferenceEnd, Array.Empty<byte>(), teacherId);
                }
            }
        });

        // Mac student: real WireClient + CameraStreamer; on ConferenceStart start the
        // peer cam (mirrors ConnectionViewModel), on ConferenceEnd stop it.
        var wire = new WireClient();
        var camera = new CameraStreamer();
        wire.EnvelopeReceived += env =>
        {
            if (env.Type == MessageType.ConferenceStart)
            {
                var sid = Guid.Empty;
                try { if (env.Payload.Length > 0) sid = MessagePackSerializer.Deserialize<ConferenceStartMessage>(env.Payload).SessionId; } catch { }
                _ = camera.StartAsync(wire, wire.EndpointId, sid, "CameraTest Mac");
            }
            else if (env.Type == MessageType.ConferenceEnd) _ = camera.StopAsync();
        };
        using var cts = new CancellationTokenSource();
        var run = wire.RunAsync("127.0.0.1", port, "CameraTest Mac", cts.Token);

        int failures = 0;
        var done = await Task.WhenAny(gotFrames.Task, Task.Delay(20000));
        if (done != gotFrames.Task) { Console.WriteLine("  ❌ timed out waiting for 12 camera frames (camera permission for this process?)"); failures++; }

        // Wait for the clean stop after ConferenceEnd.
        var stopDone = await Task.WhenAny(stopSeen.Task, Task.Delay(5000));
        if (stopDone != stopSeen.Task) Console.WriteLine("  ⚠ ConferenceCameraStop not observed within 5s");

        await camera.StopAsync();
        cts.Cancel();
        try { await run; } catch { }
        listener.Stop();

        Console.WriteLine($"\n  frames received: {received} · valid JPEG (FFD8..FFD9): {valid}/{received} · "
                        + $"correct SourceEndpointId: {endpointOk}/{received} · avg {(received > 0 ? totalBytes / received : 0)} B/frame");
        Console.WriteLine($"  ConferenceCameraStart seen: {gotStart} (src match: {startEndpoint == helloEndpoint}) · ConferenceCameraStop seen: {gotStop}");
        Console.WriteLine($"  sample → mockteacher-frames/cameratest-001.jpg");

        bool ok = failures == 0 && received >= 12 && valid == received && endpointOk == received
                  && gotStart && startEndpoint == helloEndpoint && gotStop;
        Console.WriteLine(ok
            ? "\n=== CAMERATEST PASS ✅ — Mac emits well-formed ConferenceCameraFrames + clean start/stop ==="
            : "\n=== CAMERATEST FAIL ❌ ===");
        return ok ? 0 : 1;
    }
}


// ───────────────────────────── audio self-test ─────────────────────────────
// Drives the REAL WireClient + AudioStreamer + AudioCaptureService (Sandbox assembly)
// against an in-process mock Teacher, exercising BOTH audio directions:
//   Path B (capture): server sends MicMonitorStart → the Mac captures its mic → sends
//     StudentAudioStreamStart + StudentAudioStreamFrame(PCM) → server validates format,
//     seq, RMS, routing → server sends MicMonitorStop → expects StudentAudioStreamStop.
//   Path A (playback): server sends AudioStreamStart + synthetic AudioStreamFrame(tone)
//     → the Mac decodes + enqueues for playback → server sends AudioStreamStop.
// NOTE: capture uses the physical mic, so the process needs Microphone permission
// (the .app bundle grant is bound to the bundle id, not the `dotnet` CLI host).
static class AudioTest
{
    static byte[] SyntheticTone(int seq)
    {
        var pcm = new byte[3200]; // 1600 samples, 16-bit LE
        const double step = 2.0 * Math.PI * 440.0 / 16000.0;
        for (int i = 0; i < 1600; i++)
        {
            short s = (short)(0.2 * 32767 * Math.Sin((seq * 1600 + i) * step));
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return pcm;
    }

    static double Rms(byte[] pcm)
    {
        int n = pcm.Length / 2;
        if (n == 0) return 0;
        double sum = 0;
        for (int i = 0; i < n; i++) { short s = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8)); double v = s / 32768.0; sum += v * v; }
        return Math.Sqrt(sum / n);
    }

    public static async Task<int> RunAsync(Guid teacherId)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Console.WriteLine($"=== MockTeacher --audiotest (loopback:{port}) ===");
        const int captureTarget = 15, playbackTarget = 12;

        // capture-path (B) state
        int rxB = 0, validB = 0, seqOk = 0, endpointOk = 0, nonSilent = 0, lastSeq = 0;
        long totalWireBytes = 0, totalPcmBytes = 0;
        Guid helloEndpoint = Guid.Empty;
        bool gotStartB = false, gotStopB = false, micLiveTrue = false, micLiveFalse = false;
        DateTime tFirst = default, tLast = default;
        var framesB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // playback-path (A) state
        int consumedA = 0;
        var playbackA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var serverTask = Task.Run(async () =>
        {
            var client = await listener.AcceptTcpClientAsync();
            var stream = client.GetStream();
            while (true)
            {
                var env = await Frame.ReadAsync(stream);
                if (env == null) return;
                switch (env.Type)
                {
                    case MessageType.Hello:
                        helloEndpoint = MessagePackSerializer.Deserialize<HelloMessage>(env.Payload).EndpointId;
                        Console.WriteLine($"  [Hello] ep={helloEndpoint.ToString()[..8]} → sending MicMonitorStart");
                        await Frame.SendAsync(stream, MessageType.MicMonitorStart, Array.Empty<byte>(), teacherId);
                        break;
                    case MessageType.Ping:
                        await Frame.SendAsync(stream, MessageType.Pong, Array.Empty<byte>(), teacherId);
                        break;
                    case MessageType.StudentAudioStreamStart:
                        gotStartB = true;
                        Console.WriteLine("  [StudentAudioStreamStart] (0x032B)");
                        break;
                    case MessageType.MicStateUpdate:
                    {
                        var m = MessagePackSerializer.Deserialize<MicStateUpdateMessage>(env.Payload);
                        if (m.MicLive) micLiveTrue = true; else micLiveFalse = true;
                        Console.WriteLine($"  [MicStateUpdate] MicLive={m.MicLive}");
                        break;
                    }
                    case MessageType.StudentAudioStreamFrame:
                    {
                        var f = MessagePackSerializer.Deserialize<AudioStreamFrameMessage>(env.Payload);
                        rxB++;
                        totalPcmBytes += f.PcmData.Length;
                        totalWireBytes += env.Serialize().Length + 4; // body + 4-byte length prefix
                        bool fmtOk = f.PcmData.Length == 3200 && f.SampleRate == 16000 && f.Channels == 1 && f.BitsPerSample == 16;
                        if (fmtOk) validB++;
                        if (f.FrameSeq == lastSeq + 1) seqOk++;
                        lastSeq = f.FrameSeq;
                        if (env.SenderId == helloEndpoint) endpointOk++;  // routing is via Envelope.SenderId
                        if (Rms(f.PcmData) > 0.0005) nonSilent++;
                        if (rxB == 1) tFirst = DateTime.UtcNow;
                        tLast = DateTime.UtcNow;
                        if (rxB <= 2 || rxB % 10 == 0)
                            Console.WriteLine($"  [AudioFrame B] #{rxB} seq={f.FrameSeq} {f.PcmData.Length}B {f.SampleRate}/{f.Channels}/{f.BitsPerSample} rms={Rms(f.PcmData):0.000}");
                        if (rxB >= captureTarget) framesB.TrySetResult();
                        break;
                    }
                    case MessageType.StudentAudioStreamStop:
                        gotStopB = true;
                        Console.WriteLine("  [StudentAudioStreamStop] (0x032D) → driving playback path A");
                        stopB.TrySetResult();
                        _ = Task.Run(async () =>
                        {
                            await Frame.SendAsync(stream, MessageType.AudioStreamStart, Array.Empty<byte>(), teacherId);
                            for (int i = 0; i < 14; i++)
                            {
                                await Frame.SendAsync(stream, MessageType.AudioStreamFrame,
                                    MessagePackSerializer.Serialize(new AudioStreamFrameMessage
                                    {
                                        PcmData = SyntheticTone(i), SampleRate = 16000, Channels = 1, BitsPerSample = 16,
                                        TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), FrameSeq = i + 1,
                                    }), teacherId);
                                await Task.Delay(100);
                            }
                            await Frame.SendAsync(stream, MessageType.AudioStreamStop, Array.Empty<byte>(), teacherId);
                        });
                        break;
                }

                if (rxB >= captureTarget && !stopB.Task.IsCompleted && gotStopB == false && env.Type == MessageType.StudentAudioStreamFrame && rxB == captureTarget)
                {
                    Console.WriteLine("  → capture target reached; sending MicMonitorStop");
                    await Frame.SendAsync(stream, MessageType.MicMonitorStop, Array.Empty<byte>(), teacherId);
                }
            }
        });

        // Mac student: real WireClient + AudioStreamer (capture) + AudioCaptureService (playback).
        var wire = new WireClient();
        var audioStreamer = new AudioStreamer();
        var playback = new AudioCaptureService();
        wire.EnvelopeReceived += env =>
        {
            switch (env.Type)
            {
                case MessageType.MicMonitorStart:
                    audioStreamer.StartAsync(wire, wire.EndpointId).ContinueWith(t =>
                    { if (t.Result != 0) Console.WriteLine($"  [client] mic start rc={t.Result} (Microphone permission for this process?)"); });
                    break;
                case MessageType.MicMonitorStop:
                    _ = audioStreamer.StopAsync();
                    break;
                case MessageType.AudioStreamStart:
                    _ = playback.StartPlaybackAsync(16000, 1);
                    break;
                case MessageType.AudioStreamFrame:
                    try
                    {
                        var m = MessagePackSerializer.Deserialize<AudioStreamFrameMessage>(env.Payload);
                        playback.EnqueuePcm(m.PcmData);
                        if (System.Threading.Interlocked.Increment(ref consumedA) >= playbackTarget) playbackA.TrySetResult();
                    }
                    catch { }
                    break;
                case MessageType.AudioStreamStop:
                    _ = playback.StopPlaybackAsync();
                    break;
            }
        };
        using var cts = new CancellationTokenSource();
        var run = wire.RunAsync("127.0.0.1", port, "AudioTest Mac", cts.Token);

        int failures = 0;

        // ── Path B: capture ──
        var doneB = await Task.WhenAny(framesB.Task, Task.Delay(20000));
        if (doneB != framesB.Task) { Console.WriteLine("  ❌ timed out waiting for capture frames (Microphone permission for this process?)"); failures++; }
        await Task.WhenAny(stopB.Task, Task.Delay(5000));

        double seconds = (tLast - tFirst).TotalSeconds;
        double wireKbps = seconds > 0 ? totalWireBytes * 8.0 / seconds / 1000.0 : 0;
        double pcmKbps = seconds > 0 ? totalPcmBytes * 8.0 / seconds / 1000.0 : 0;

        // ── Path A: playback — the server emits AudioStreamStart/Frame/Stop once capture
        //    stops (see the StudentAudioStreamStop case); the client consumes + enqueues. ──
        var doneA = await Task.WhenAny(playbackA.Task, Task.Delay(8000));
        if (doneA != playbackA.Task) { Console.WriteLine($"  ⚠ playback consumed {consumedA}/{playbackTarget} frames"); }

        await audioStreamer.StopAsync();
        await playback.StopPlaybackAsync();
        cts.Cancel();
        try { await run; } catch { }
        listener.Stop();

        Console.WriteLine($"\n  [B capture] frames={rxB} validFmt={validB}/{rxB} seq↑={seqOk}/{rxB} "
                        + $"endpoint={endpointOk}/{rxB} nonSilent={nonSilent}/{rxB}");
        Console.WriteLine($"  [B lifecycle] Start={gotStartB} Stop={gotStopB} MicLive(true→false)={micLiveTrue}/{micLiveFalse}");
        Console.WriteLine($"  [A playback] consumed={consumedA}/{playbackTarget}");
        Console.WriteLine($"  [bandwidth] wire ~{wireKbps:0} kbit/s · raw PCM ~{pcmKbps:0} kbit/s · avg {(rxB > 0 ? totalWireBytes / rxB : 0)} B/frame on wire (PCM 3200 B)");

        bool ok = failures == 0 && rxB >= captureTarget && validB == rxB && seqOk == rxB
                  && endpointOk == rxB && nonSilent > 0 && gotStartB && gotStopB
                  && micLiveTrue && micLiveFalse && consumedA >= playbackTarget;
        Console.WriteLine(ok
            ? "\n=== AUDIOTEST PASS ✅ — Mac emits well-formed PCM talkback + consumes teacher audio (both directions) ==="
            : "\n=== AUDIOTEST FAIL ❌ ===");
        return ok ? 0 : 1;
    }
}
