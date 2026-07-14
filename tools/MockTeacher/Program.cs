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
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;

int port = 7777;
bool selfTest = false, streamTest = false, streamTestH264 = false, cameraTest = false, audioTest = false, lockTest = false, inputTest = false, inputHold = false, configTest = false, trayTest = false, permTest = false, launchAgentTest = false;
int holdSeconds = 20;
string? recvMode = null;   // TT-0-C: --recv <screen|screen-h264|camera|audio> — receive + assert frames from an EXTERNAL student (MockStudent)
bool recvMany = false; int recvDuration = 10;   // TT-0-D: --recvmany — accept N students, count frames (harness-scale proof)
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
        case "--locktest": lockTest = true; break;
        case "--inputtest": inputTest = true; break;
        case "--inputhold":
            inputHold = true;
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var hs)) { holdSeconds = hs; i++; }
            break;
        case "--configtest": configTest = true; break;
        case "--traytest": trayTest = true; break;
        case "--permtest": permTest = true; break;
        case "--launchagenttest": launchAgentTest = true; break;
        case "--recv": recvMode = args[++i]; break;
        case "--recvmany": recvMany = true; break;
        case "--duration": recvDuration = int.Parse(args[++i]); break;
    }
}

var teacherId = Guid.NewGuid();

if (recvMode != null) return await RecvTest.RunAsync(recvMode, port, teacherId);
if (recvMany) return await RecvManyTest.RunAsync(port, recvDuration, teacherId);
if (launchAgentTest) return LaunchAgentTest.Run();
if (permTest) return PermTest.Run();
if (trayTest) return TrayTest.Run();
if (configTest) return ConfigTest.Run();
if (inputHold) return await InputTest.RunHoldAsync(holdSeconds);
if (inputTest) return await InputTest.RunAsync();
if (lockTest) return await LockTest.RunAsync();
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

    /// <summary>
    /// Broadcast a real file to the connected student as the shipped Teacher does:
    /// FileAnnounce (with SHA-256 + chunk count) → FileChunk × N (64 KB) → FileComplete.
    /// The macOS daemon reassembles + verifies + saves it, then toasts the Agent.
    /// </summary>
    public static async Task SendFileAsync(NetworkStream s, string path, Guid sender, CancellationToken ct = default)
    {
        const int ChunkSize = 64 * 1024;
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var transferId = Guid.NewGuid();
        var name = Path.GetFileName(path);
        int chunkCount = (bytes.Length + ChunkSize - 1) / ChunkSize;

        await SendAsync(s, MessageType.FileAnnounce, MessagePackSerializer.Serialize(new FileAnnounceMessage
        { TransferId = transferId, FileName = name, SizeBytes = bytes.Length, Sha256Hex = sha, ChunkCount = chunkCount }), sender, ct);

        for (int i = 0; i < chunkCount; i++)
        {
            int off = i * ChunkSize, len = Math.Min(ChunkSize, bytes.Length - off);
            var data = new byte[len];
            Buffer.BlockCopy(bytes, off, data, 0, len);
            await SendAsync(s, MessageType.FileChunk, MessagePackSerializer.Serialize(new FileChunkMessage
            { TransferId = transferId, ChunkIndex = i, Data = data }), sender, ct);
        }

        await SendAsync(s, MessageType.FileComplete, MessagePackSerializer.Serialize(new FileCompleteMessage
        { TransferId = transferId, FileName = name }), sender, ct);
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
                else if (line == "lockdemo")
                {
                    // Self-releasing lock: kiosk shield up, then AUTO-UNLOCK after 8 s. Safe on a single
                    // Mac — the shield hides the menu bar + disables Cmd+Tab, so you can't reach this
                    // terminal to type "unlock" while it's up. (Backstops if this ever fails: quit
                    // MockTeacher → dead-man auto-unlocks after 45 s; or kill the daemon → OS releases.)
                    await Frame.SendAsync(live, MessageType.LockScreen, Frame.LockPayload(), teacherId);
                    Console.WriteLine("→ LockScreen (auto-unlock in 8 s) …");
                    await Task.Delay(8000);
                    await Frame.SendAsync(live, MessageType.UnlockScreen, Frame.LockPayload(), teacherId);
                    Console.WriteLine("→ UnlockScreen (auto)");
                }
                else if (line.StartsWith("file "))
                {
                    var path = line[5..].Trim().Trim('"');
                    if (!File.Exists(path)) Console.WriteLine($"(no such file: {path})");
                    else { await Frame.SendFileAsync(live, path, teacherId); Console.WriteLine($"→ File broadcast: {Path.GetFileName(path)}"); }
                }
                else if (line == "quiz")
                {
                    var q = new QuizQuestionMessage
                    {
                        QuizId = Guid.NewGuid(),
                        Question = "What is the capital of Thailand?",
                        Options = new() { "Bangkok", "Chiang Mai", "Phuket", "Khon Kaen" },
                    };
                    await Frame.SendAsync(live, MessageType.QuizStart, MessagePackSerializer.Serialize(q), teacherId);
                    Console.WriteLine($"→ QuizStart: {q.Question}");
                }
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
                else if (env.Type is MessageType.ChatBroadcast or MessageType.ChatDirect)
                {
                    var chat = MessagePackSerializer.Deserialize<ChatMessage>(env.Payload);
                    Console.WriteLine($"[Chat] {chat.SenderName}: {chat.Text}");
                }
                else if (env.Type == MessageType.QuizAnswerSubmit)
                {
                    var a = MessagePackSerializer.Deserialize<QuizAnswerMessage>(env.Payload);
                    Console.WriteLine($"[Quiz] {a.StudentName} answered #{a.SelectedIndex} '{a.SelectedText}'");
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


// ───────────────────────────── screen-lock dead-man self-test ─────────────────────────────
// Drives the REAL LockService (Sandbox assembly) with simulated WireClient status
// transitions to prove the four-layer dead-man switch — specifically the disconnect-grace
// blip-vs-death distinction and the continuous (non-reset) window. Uses a SHRUNK grace
// (3s) but the SAME transition logic. No AppKit main loop runs here, so the native shield
// never actually renders (DispatchQueue.main.async is never pumped) — zero screen-takeover
// risk; nty_lock_is_shown() tracks LockService's show/hide decisions (the dead-man logic).
static class LockTest
{
    // Build a LockService driven ENTIRELY by flag backends — zero AppKit, ZERO real tap. Proves the
    // shield dead-man logic (30-E) AND the input-guard lifecycle (31-D) with no OS side effects. The
    // native shield RENDERING (30-B/30-F) and the native tap (31-B --inputtest) are proven separately.
    static (LockService svc, Func<bool> shield, Func<bool> guard, Func<int> prompts) MakeSvc(
        int graceMs, int capMs, bool trusted)
    {
        int shieldFlag = 0, guardFlag = 0, trustedFlag = trusted ? 1 : 0, prompts = 0;
        var svc = new LockService(graceMs, capMs,
            shieldShow:   _  => Interlocked.Exchange(ref shieldFlag, 1),
            shieldHide:   () => Interlocked.Exchange(ref shieldFlag, 0),
            shieldIsShown: () => Volatile.Read(ref shieldFlag) == 1,
            guardTrusted: () => Volatile.Read(ref trustedFlag) == 1,
            guardPrompt:  () => Interlocked.Increment(ref prompts),
            guardInstall: () => { if (Volatile.Read(ref trustedFlag) == 1) { Interlocked.Exchange(ref guardFlag, 1); return true; } return false; },
            guardRemove:  () => Interlocked.Exchange(ref guardFlag, 0),
            guardIsActive: () => Volatile.Read(ref guardFlag) == 1);
        return (svc,
                () => Volatile.Read(ref shieldFlag) == 1,
                () => Volatile.Read(ref guardFlag) == 1,
                () => Volatile.Read(ref prompts));
    }

    public static async Task<int> RunAsync()
    {
        int failures = 0;
        void Check(string name, bool cond)
        {
            if (cond) Console.WriteLine($"  ✅ {name}");
            else { Console.WriteLine($"  ❌ {name}"); failures++; }
        }

        Console.WriteLine("=== MockTeacher --locktest (flag backends — shield dead-man + input-guard lifecycle; NO AppKit, ZERO real tap) ===");

        // Main service: trusted classroom Mac (Accessibility granted), grace 3s, cap 10min.
        var (svc, Shield, Guard, _) = MakeSvc(graceMs: 3000, capMs: 600_000, trusted: true);
        int shownFlag = 0;
        svc.LockStateChanged += up => Volatile.Write(ref shownFlag, up ? 1 : 0);
        svc.Log += m => Console.WriteLine($"        [lock] {m}");
        bool Evt() => Volatile.Read(ref shownFlag) == 1;   // LockStateChanged-tracked

        // ── CASE A: Wi-Fi blip → lock + guard HOLD; explicit unlock releases BOTH ──
        Console.WriteLine("\n-- CASE A: Wi-Fi blip (reconnect within grace) — shield + guard HELD, explicit unlock releases both --");
        svc.Lock(null);
        Check("A: shield shown after LockScreen", Shield() && Evt());
        Check("A: guard INSTALLED with lock (trusted)", Guard());
        svc.OnConnectionStatus(WireStatus.Reconnecting);        // t0: grace starts
        await Task.Delay(1000);
        svc.OnConnectionStatus(WireStatus.Connected);           // t1.0: reconnect within grace → cancel
        await Task.Delay(300);
        Check("A: shield HELD immediately after reconnect", Shield() && Evt());
        Check("A: guard HELD after reconnect", Guard());
        await Task.Delay(2500);                                 // t3.8: past the original 3s window
        Check("A: STILL HELD past original grace (blip did NOT unlock)", Shield() && Evt() && Guard());
        svc.Unlock();
        Check("A: explicit Unlock hides shield", !Shield() && !Evt());
        Check("A: explicit Unlock RELEASES guard", !Guard());

        // ── CASE B: teacher-death → grace fire releases shield + guard ──
        Console.WriteLine("\n-- CASE B: teacher-death (sustained disconnect > grace) — grace fire releases shield + guard --");
        svc.Lock(null);
        Check("B: shield + guard up on lock", Shield() && Guard());
        svc.OnConnectionStatus(WireStatus.Reconnecting);        // t0
        await Task.Delay(500);
        svc.OnConnectionStatus(WireStatus.Disconnected);        // t0.5: continuous
        Check("B: still shown mid-grace", Shield() && Guard());
        await Task.Delay(3200);                                 // t3.7 from t0
        Check("B: shield AUTO-UNLOCKED (grace fired at ~3s)", !Shield() && !Evt());
        Check("B: guard RELEASED on grace fire", !Guard());

        // ── CASE C: no-reset (continuous window) → grace fire still releases guard ──
        Console.WriteLine("\n-- CASE C: no-reset (timer NOT restarted on Reconnecting→Disconnected) --");
        svc.Lock(null);
        svc.OnConnectionStatus(WireStatus.Reconnecting);        // t0: grace starts
        await Task.Delay(1000);
        svc.OnConnectionStatus(WireStatus.Disconnected);        // t1.0: must NOT restart
        await Task.Delay(1600);                                 // t2.6: before 3s-from-t0
        Check("C: still shown at t=2.6s (has NOT fired early)", Shield() && Guard());
        await Task.Delay(900);                                  // t3.5: past 3s-from-t0, before 4s (reset-bug would fire at t4.0)
        Check("C: shield AUTO-UNLOCKED by t=3.5s → window ran from FIRST departure, not from Disconnected", !Shield() && !Evt());
        Check("C: guard RELEASED with it", !Guard());

        // ── CASE D: max-duration cap fire → releases shield + guard (independent of connection) ──
        Console.WriteLine("\n-- CASE D: max-duration cap fire (cap=1500ms) — releases shield + guard, no disconnect needed --");
        var (svcD, ShieldD, GuardD, _) = MakeSvc(graceMs: 600_000, capMs: 1500, trusted: true);
        svcD.Log += m => Console.WriteLine($"        [lockD] {m}");
        svcD.Lock(null);
        Check("D: shield + guard up on lock", ShieldD() && GuardD());
        await Task.Delay(2100);                                 // > 1500ms cap, never touched the connection
        Check("D: shield released on cap fire", !ShieldD());
        Check("D: guard RELEASED on cap fire", !GuardD());

        // ── CASE E: Accessibility DENIED → lock still works, guard NOT installed (graceful degrade) ──
        Console.WriteLine("\n-- CASE E: Accessibility DENIED — lock enforced, guard NOT installed (graceful degrade) --");
        var (svcE, ShieldE, GuardE, PromptsE) = MakeSvc(graceMs: 3000, capMs: 600_000, trusted: false);
        svcE.Log += m => Console.WriteLine($"        [lockE] {m}");
        svcE.Lock(null);
        Check("E: shield UP (lock enforced WITHOUT Accessibility)", ShieldE());
        Check("E: guard NOT installed (graceful degrade)", !GuardE());
        Check("E: prompted for Accessibility exactly once", PromptsE() == 1);
        svcE.Unlock();
        Check("E: unlock clean (shield down, guard absent)", !ShieldE() && !GuardE());

        Console.WriteLine(failures == 0
            ? "\n=== LOCKTEST PASS ✅ — dead-man grace (blip HOLDS / death+cap UNLOCK, continuous) · guard installed-on-lock & RELEASED on all 3 dead-man paths (explicit/grace/cap) · degrades gracefully ==="
            : $"\n=== LOCKTEST FAIL ❌ ({failures} check(s)) ===");
        return failures == 0 ? 0 : 1;
    }
}

// ── Phase 31-B — input-guard (CGEventTap) SAFETY GATE (--inputtest) ────────────────
// Proves the keystroke guard is safe BEFORE it is ever tied to a lock:
//   • guaranteed uninstall — the real-tap section is wrapped in try/finally with an
//     unconditional, bounded nty_input_guard_stop() (native joins the tap thread ≤2s);
//   • FAIL-OPEN demonstrated two ways — deterministically (force-disable → is_enabled 0
//     = keys flow → re-enable) AND live (stall the callback → the OS auto-disables the
//     slow tap → keys flow → the callback re-enables). A wedged keyboard is unreachable.
//   • graceful degrade — with no Accessibility for THIS binary, start() returns -1 and
//     NO real tap is installed; the harness asserts that safe path and exits PASS.
// Installing a real tap needs Accessibility for the launching binary (here the MockTeacher
// exe / dotnet host). Process-kill releases the tap regardless (OS-enforced).
static partial class InputTest
{
    private const string Lib = "NtyCapture";
    [LibraryImport(Lib)] private static partial int  nty_accessibility_check();
    [LibraryImport(Lib)] private static partial int  nty_accessibility_request();
    [LibraryImport(Lib)] private static partial int  nty_input_guard_start();
    [LibraryImport(Lib)] private static partial void nty_input_guard_stop();
    [LibraryImport(Lib)] private static partial int  nty_input_guard_is_active();
    [LibraryImport(Lib)] private static partial int  nty_input_guard_is_enabled();
    [LibraryImport(Lib)] private static partial long nty_input_guard_suppress_count();
    [LibraryImport(Lib)] private static partial long nty_input_guard_disabled_count();
    [LibraryImport(Lib)] private static partial void nty_input_guard_set_stall_ms(int ms);
    [LibraryImport(Lib)] private static partial void nty_input_test_synthesize(int keycode, ulong flags, int count);

    const ulong FlagControl = 0x040000;   // CGEventFlags.maskControl raw bit
    const int   KcLeft      = 123;         // kVK_LeftArrow → Ctrl+Left = Spaces-left (in the suppress list, inert if it ever leaks on a single Space)

    public static async Task<int> RunAsync()
    {
        int failures = 0;
        void Check(string name, bool cond)
        {
            if (cond) Console.WriteLine($"  ✅ {name}");
            else { Console.WriteLine($"  ❌ {name}"); failures++; }
        }

        Console.WriteLine("=== MockTeacher --inputtest (Phase 31-B — CGEventTap SAFETY GATE) ===");
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("  (skipped — not macOS)"); return 0; }

        int trusted = nty_accessibility_check();
        Console.WriteLine($"  Accessibility trusted for this binary: {(trusted == 1 ? "YES" : "NO")}");

        // ── graceful-degrade path (the SAFE default — no real tap is ever installed) ──
        if (trusted != 1)
        {
            Console.WriteLine("\n-- Accessibility NOT granted → asserting graceful degrade (no tap installed, nothing to strand) --");
            nty_accessibility_request();                       // surface the system prompt for a later run
            int rc = nty_input_guard_start();
            Check("degrade: start() returns -1 (not trusted)", rc == -1);
            Check("degrade: is_active()==0 (no tap installed)", nty_input_guard_is_active() == 0);
            nty_input_guard_stop();                            // unconditional — safe even when idle
            Check("degrade: stop() safe when idle (still 0)", nty_input_guard_is_active() == 0);
            Console.WriteLine("\n  ℹ To run the FULL fail-open demo, grant Accessibility to this binary:");
            Console.WriteLine("     System Settings ▸ Privacy & Security ▸ Accessibility → enable the prompted entry,");
            Console.WriteLine("     then re-run:  dotnet run --project tools/MockTeacher -- --inputtest");
            Console.WriteLine(failures == 0
                ? "\n=== INPUTTEST PASS ✅ (graceful-degrade path proven — grant Accessibility for the live fail-open demo) ==="
                : $"\n=== INPUTTEST FAIL ❌ ({failures} check(s)) ===");
            return failures == 0 ? 0 : 1;
        }

        // ── trusted → full real-tap demo, GUARANTEED-UNINSTALL via try/finally ──
        try
        {
            int rc = nty_input_guard_start();
            Check("start() returns 0", rc == 0);
            Check("is_active()==1 after start", nty_input_guard_is_active() == 1);
            Check("is_enabled()==1 after start (guarding)", nty_input_guard_is_enabled() == 1);

            // (1) suppression — synthesize Ctrl+Left ×3; the tap should swallow all three.
            long s0 = nty_input_guard_suppress_count();
            nty_input_test_synthesize(KcLeft, FlagControl, 3);
            await Task.Delay(500);
            long swallowed = nty_input_guard_suppress_count() - s0;
            Check($"suppressed 3 synthesized Ctrl+Left ({swallowed} swallowed)", swallowed >= 3);
            nty_input_guard_stop();
            Check("is_active()==0 after suppression-phase stop", nty_input_guard_is_active() == 0);

            // (2) FAIL-OPEN — the real thing, via the OS watchdog. Arm a 2000ms stall BEFORE start()
            //     (thread creation publishes it to the tap thread), then drive events through the now-
            //     slow tap. A callback that doesn't return in ~1s is AUTO-DISABLED by the OS — during
            //     that window keystrokes flow untouched (fail-open) — and the callback re-enables on
            //     the .tapDisabledByTimeout it receives. This is exactly the wedged-tap failure mode,
            //     and macOS resolves it in the user's favor automatically: a frozen keyboard is NOT a
            //     reachable state.
            Console.WriteLine("\n-- FAIL-OPEN (live, real OS watchdog): callback stalls 2000ms → OS auto-disables the slow tap --");
            nty_input_guard_set_stall_ms(2000);
            int rc2 = nty_input_guard_start();
            Check("live: start() with stall armed returns 0", rc2 == 0 && nty_input_guard_is_active() == 1);
            long w0 = nty_input_guard_disabled_count();
            for (int i = 0; i < 6; i++) { nty_input_test_synthesize(KcLeft, FlagControl, 1); await Task.Delay(600); }
            await Task.Delay(2500);
            long autoDisabled = nty_input_guard_disabled_count() - w0;
            Console.WriteLine($"  → the OS auto-disabled our slow tap {autoDisabled}× — each is a window where keystrokes flowed untouched; the callback then re-enabled.");
            Check("live: OS auto-disabled the slow tap ≥1× (wedged tap NOT reachable — it FAILS OPEN)", autoDisabled >= 1);
            nty_input_guard_set_stall_ms(0);
            nty_input_guard_stop();
            await Task.Delay(500);   // let the stalled tap thread finish teardown before recovery

            // (3) RECOVERY — after the fail-open disable/re-enable cycles, a fresh guard suppresses
            //     normally again (end-to-end: guards → fails open under stall → guards again).
            int rc3 = nty_input_guard_start();
            Check("recovery: start() returns 0", rc3 == 0);
            long r0 = nty_input_guard_suppress_count();
            nty_input_test_synthesize(KcLeft, FlagControl, 3);
            await Task.Delay(500);
            long recov = nty_input_guard_suppress_count() - r0;
            Check($"recovery: guard suppresses again after fail-open ({recov} swallowed)", recov >= 3);
        }
        finally
        {
            // GUARANTEED uninstall — unconditional + bounded (native joins the tap thread ≤2s).
            nty_input_guard_set_stall_ms(0);
            nty_input_guard_stop();
        }
        Check("is_active()==0 after stop (guaranteed uninstall)", nty_input_guard_is_active() == 0);
        Check("is_enabled()==0 after stop (tap gone)", nty_input_guard_is_enabled() == 0);

        Console.WriteLine(failures == 0
            ? "\n=== INPUTTEST PASS ✅ — suppression works · FAILS OPEN (OS auto-disable + re-enable) · guaranteed uninstall ==="
            : $"\n=== INPUTTEST FAIL ❌ ({failures} check(s)) ===");
        return failures == 0 ? 0 : 1;
    }

    // Process-kill release demo (--inputhold [seconds]). Installs a REAL tap and holds it on a
    // BOUNDED timer so you can `kill -9 <pid>` the process and confirm the tap dies with it —
    // the OS-enforced ultimate backstop (same ownership guarantee as the Phase 30 shield). If it
    // is never killed, the timer + finally + process-exit each release it — it can never strand.
    public static async Task<int> RunHoldAsync(int seconds)
    {
        Console.WriteLine("=== MockTeacher --inputhold (Phase 31-B — process-kill release demo) ===");
        if (!OperatingSystem.IsMacOS()) { Console.WriteLine("  (skipped — not macOS)"); return 0; }
        if (nty_accessibility_check() != 1)
        {
            int rc0 = nty_input_guard_start();     // returns -1, installs nothing (graceful degrade)
            Console.WriteLine($"  Accessibility NOT granted → start()={rc0}, no tap installed. Grant it to run this demo.");
            return 0;
        }
        int pid = Environment.ProcessId;
        try
        {
            int rc = nty_input_guard_start();
            if (rc != 0) { Console.WriteLine($"  ❌ start() failed rc={rc}"); return 1; }
            Console.WriteLine($"  ✅ TAP INSTALLED — pid={pid}, is_active={nty_input_guard_is_active()}, is_enabled={nty_input_guard_is_enabled()}");
            Console.WriteLine( "     Spotlight (Cmd+Space) + Ctrl+Arrows are SUPPRESSED while this process lives.");
            Console.WriteLine($"     → PROVE process-death frees the keyboard:   kill -9 {pid}");
            Console.WriteLine( "       then press Cmd+Space — Spotlight opens again (the tap died with the process).");
            Console.WriteLine($"     (auto-releases in {seconds}s regardless — bounded, never strands.)");
            for (int i = seconds; i > 0; i--) await Task.Delay(1000);
            Console.WriteLine("  timeout reached — auto-releasing.");
        }
        finally
        {
            nty_input_guard_stop();
            Console.WriteLine($"  tap released; is_active={nty_input_guard_is_active()}");
        }
        return 0;
    }
}

// ── Phase 32-B — StudentConfig persistence self-test (--configtest) ────────────────
// Exercises the real StudentConfig (Sandbox assembly) against a TEMP directory so it never
// touches the user's ~/Library/Application Support config. Proves: round-trip, missing→defaults
// (host-name display name), corrupt→defaults+logged (no throw), and the documented admin
// pre-seed (hand-authored camelCase JSON) parses.
static class ConfigTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool cond)
        {
            if (cond) Console.WriteLine($"  ✅ {name}");
            else { Console.WriteLine($"  ❌ {name}"); failures++; }
        }

        var dir = Path.Combine(Path.GetTempPath(), "nty-configtest-" + Guid.NewGuid().ToString("N"));
        var host = Environment.MachineName;
        Console.WriteLine($"=== MockTeacher --configtest (temp dir; host name = {host}) ===");
        try
        {
            // (1) round-trip: write → read → matches
            new StudentConfig { TeacherIp = "10.0.0.5", Port = 9999, DisplayName = "LAB-42", ChannelId = "777" }.Save(dir);
            var r = StudentConfig.Load(dir);
            Check("round-trip: teacherIp", r.TeacherIp == "10.0.0.5");
            Check("round-trip: port", r.Port == 9999);
            Check("round-trip: displayName", r.DisplayName == "LAB-42");
            Check("round-trip: channelId", r.ChannelId == "777");

            // (2) missing file → defaults (display name = host name)
            var missingDir = Path.Combine(Path.GetTempPath(), "nty-configtest-missing-" + Guid.NewGuid().ToString("N"));
            var d = StudentConfig.Load(missingDir);
            Check("missing: teacherIp empty (not yet configured)", d.TeacherIp == "");
            Check("missing: port default 7777", d.Port == 7777);
            Check($"missing: displayName = host name", d.DisplayName == host);
            Check("missing: channelId default 1234", d.ChannelId == "1234");

            // (3) corrupt file → defaults, NO throw, logged
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ not valid json ]]");
            string? corruptLog = null;
            var c = StudentConfig.Load(dir, m => corruptLog = m);
            Check("corrupt: returned defaults, did NOT throw", c.DisplayName == host && c.Port == 7777);
            Check("corrupt: logged 'unreadable'", corruptLog is not null && corruptLog.Contains("unreadable"));

            // (4) admin pre-seed: hand-authored camelCase JSON parses
            File.WriteAllText(Path.Combine(dir, "config.json"),
                "{ \"teacherIp\": \"172.20.10.7\", \"port\": 7777, \"displayName\": \"SEEDED-01\", \"channelId\": \"1234\" }");
            var seed = StudentConfig.Load(dir);
            Check("admin pre-seed: teacherIp parsed", seed.TeacherIp == "172.20.10.7");
            Check("admin pre-seed: displayName parsed", seed.DisplayName == "SEEDED-01");

            // (5) normalize: blank displayName + missing port in file → host name + 7777
            File.WriteAllText(Path.Combine(dir, "config.json"), "{ \"teacherIp\": \"1.2.3.4\", \"displayName\": \"  \" }");
            var norm = StudentConfig.Load(dir);
            Check("normalize: blank displayName → host name", norm.DisplayName == host);
            Check("normalize: missing port → 7777", norm.Port == 7777);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }

        Console.WriteLine(failures == 0
            ? "\n=== CONFIGTEST PASS ✅ — round-trip · missing→defaults(host name) · corrupt→defaults+logged · admin pre-seed camelCase ==="
            : $"\n=== CONFIGTEST FAIL ❌ ({failures} check(s)) ===");
        return failures == 0 ? 0 : 1;
    }
}

// ── Phase 32-C — TrayPresenter state→glyph mapping self-test (--traytest) ──────────
// Proves the pure state logic behind the menubar tray (connection status + lock → glyph kind +
// tooltip + status line), including lock-precedence. The icon RENDERING (drawn dot/padlock) and
// the menu interaction (Show Debug Window / Quit) are visual → verified LIVE, not here.
static class TrayTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool cond)
        {
            if (cond) Console.WriteLine($"  ✅ {name}");
            else { Console.WriteLine($"  ❌ {name}"); failures++; }
        }

        Console.WriteLine("=== MockTeacher --traytest (TrayPresenter state→glyph/tooltip mapping) ===");

        var dis = TrayPresenter.Describe(WireStatus.Disconnected, false, "172.20.10.7");
        Check("disconnected → Disconnected kind", dis.kind == TrayPresenter.Kind.Disconnected);
        Check("disconnected → status line 'Disconnected'", dis.statusLine == "Disconnected");

        var con = TrayPresenter.Describe(WireStatus.Connecting, false, "1.2.3.4");
        Check("connecting → Connecting kind", con.kind == TrayPresenter.Kind.Connecting);
        var rec = TrayPresenter.Describe(WireStatus.Reconnecting, false, "1.2.3.4");
        Check("reconnecting → Connecting kind", rec.kind == TrayPresenter.Kind.Connecting);

        var ok = TrayPresenter.Describe(WireStatus.Connected, false, "172.20.10.7");
        Check("connected → Connected kind", ok.kind == TrayPresenter.Kind.Connected);
        Check("connected → status line has IP", ok.statusLine.StartsWith("Connected") && ok.statusLine.Contains("172.20.10.7"));
        Check("connected → tooltip has IP", ok.tooltip.Contains("172.20.10.7"));

        var lockedUp = TrayPresenter.Describe(WireStatus.Connected, true, "172.20.10.7");
        Check("locked over CONNECTED → Locked wins", lockedUp.kind == TrayPresenter.Kind.Locked);
        Check("locked → status line shows Locked", lockedUp.statusLine.Contains("Locked"));

        var lockedDown = TrayPresenter.Describe(WireStatus.Disconnected, true, "");
        Check("locked over DISCONNECTED → Locked wins (enforced status is salient)", lockedDown.kind == TrayPresenter.Kind.Locked);

        Console.WriteLine(failures == 0
            ? "\n=== TRAYTEST PASS ✅ — connect/connecting/disconnect mapping + lock-precedence + IP in status ==="
            : $"\n=== TRAYTEST FAIL ❌ ({failures} check(s)) ===");
        return failures == 0 ? 0 : 1;
    }
}

// ── Phase 32-D — permissions onboarding logic self-test (--permtest) ──────────────
// Proves the pure PermissionPresenter mapping (state→pill), the run-readiness rule (only Screen
// Recording gates it), and the 4-row descriptors (tiers + the Screen relaunch flag + primary-first
// order). The live TCC grant/deny flow and the window rendering are visual → verified LIVE.
static class PermTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool cond)
        {
            if (cond) Console.WriteLine($"  ✅ {name}");
            else { Console.WriteLine($"  ❌ {name}"); failures++; }
        }

        Console.WriteLine("=== MockTeacher --permtest (PermissionPresenter mapping + tiers + ready logic) ===");

        // status pill mapping (glyph, text, style-class)
        Check("Granted → 🟢 granted",       PermissionPresenter.Pill(PermState.Granted)      == ("🟢", "Granted", "granted"));
        Check("Denied → 🔴 denied",         PermissionPresenter.Pill(PermState.Denied)       == ("🔴", "Denied", "denied"));
        Check("NotDetermined → ⚪ not req.", PermissionPresenter.Pill(PermState.NotDetermined) == ("⚪", "Not requested", "unknown"));
        Check("Unsupported → ⚪ unsupported",PermissionPresenter.Pill(PermState.Unsupported)  == ("⚪", "Unsupported", "unknown"));

        // run-readiness gates ONLY on Screen Recording (optional perms never block)
        Check("ready when Screen granted",           PermissionPresenter.IsReadyToRun(PermState.Granted));
        Check("NOT ready when Screen not-determined", !PermissionPresenter.IsReadyToRun(PermState.NotDetermined));
        Check("NOT ready when Screen denied",         !PermissionPresenter.IsReadyToRun(PermState.Denied));

        // descriptors: 4 rows, correct tiers, Screen primary + needs relaunch
        Check("4 permission rows", Permissions.All.Count == 4);
        Check("Screen is FIRST (primary)", Permissions.All[0].Id == PermId.Screen);
        var byId = Permissions.All.ToDictionary(p => p.Id);
        Check("Screen = Required + needs relaunch", byId[PermId.Screen].Tier == PermTier.Required && byId[PermId.Screen].NeedsRelaunch);
        Check("Camera = Optional, no relaunch",     byId[PermId.Camera].Tier == PermTier.Optional && !byId[PermId.Camera].NeedsRelaunch);
        Check("Microphone = Optional, no relaunch",  byId[PermId.Microphone].Tier == PermTier.Optional && !byId[PermId.Microphone].NeedsRelaunch);
        Check("Accessibility = Optional (graceful degrade), no relaunch", byId[PermId.Accessibility].Tier == PermTier.Optional && !byId[PermId.Accessibility].NeedsRelaunch);

        Console.WriteLine(failures == 0
            ? "\n=== PERMTEST PASS ✅ — pill mapping · Screen-only readiness · 4 rows (Screen primary+relaunch, rest optional) ==="
            : $"\n=== PERMTEST FAIL ❌ ({failures} check(s)) ===");
        return failures == 0 ? 0 : 1;
    }
}

// ── Phase 32-E — LaunchAgent plist + install/uninstall self-test (--launchagenttest) ──
// SAFE by construction: runs against a TEMP LaunchAgents dir, a FAKE launchctl runner, and a FAKE
// bundled program path — installs ZERO real agents, runs NO real launchctl, leaves no trace. Proves
// the plist XML (RunAtLoad=true, KeepAlive=false, self-targeted exe), enable→present+bootstrap,
// disable→removed+bootout (uninstall = no trace), and the IsBundled guard (dev path refused).
static class LaunchAgentTest
{
    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool cond)
        {
            if (cond) Console.WriteLine($"  ✅ {name}");
            else { Console.WriteLine($"  ❌ {name}"); failures++; }
        }

        Console.WriteLine("=== MockTeacher --launchagenttest (plist + install/uninstall — TEMP dir, FAKE launchctl, ZERO real agents) ===");

        var dir = Path.Combine(Path.GetTempPath(), "nty-launchagent-" + Guid.NewGuid().ToString("N"));
        var calls = new List<string>();
        int FakeCtl(string sub, string[] args) { calls.Add(sub + " " + string.Join(" ", args)); return 0; }
        const string bundled = "/Applications/NTY ClassroomCtrl.app/Contents/MacOS/ClassroomCtrl.Avalonia.Sandbox";

        try
        {
            var mgr = new LaunchAgentManager(agentsDir: dir, programPath: bundled, runctl: FakeCtl);

            // (1) plist XML: valid + correct key→value pairing (RunAtLoad=true, KeepAlive=false)
            var xml = mgr.BuildPlistXml();
            var doc = System.Xml.Linq.XDocument.Parse(xml);   // throws on malformed XML
            var kids = doc.Descendants("dict").First().Elements().ToList();
            bool Pair(string key, string tag)
            {
                int i = kids.FindIndex(e => e.Name.LocalName == "key" && e.Value == key);
                return i >= 0 && i + 1 < kids.Count && kids[i + 1].Name.LocalName == tag;
            }
            bool PairString(string key, string val)
            {
                int i = kids.FindIndex(e => e.Name.LocalName == "key" && e.Value == key);
                return i >= 0 && i + 1 < kids.Count && kids[i + 1].Name.LocalName == "string" && kids[i + 1].Value == val;
            }
            Check("plist is valid XML with <plist> root", doc.Root!.Name.LocalName == "plist");
            Check("Label = com.nty.classroomctrl.student", PairString("Label", "com.nty.classroomctrl.student"));
            Check("RunAtLoad = true", Pair("RunAtLoad", "true"));
            Check("KeepAlive = false (locked decision)", Pair("KeepAlive", "false"));
            Check("ProgramArguments self-targets the .app exe", xml.Contains(bundled));

            // (2) bundle gate + initial state
            Check("IsBundled true for a .app path", mgr.IsBundled);
            Check("initially disabled (no plist yet)", !mgr.IsEnabled);

            // (3) enable → plist written + launchctl bootstrap invoked
            Check("enable() ok", mgr.Enable().ok);
            Check("enabled → plist present", mgr.IsEnabled && File.Exists(mgr.PlistPath));
            Check("enable ran `launchctl bootstrap gui/…`", calls.Exists(c => c.StartsWith("bootstrap gui/")));

            // (4) disable → plist REMOVED (no trace) + bootout invoked  ← borrowed-Mac safety
            calls.Clear();
            Check("disable() ok", mgr.Disable().ok);
            Check("disabled → plist REMOVED (leaves no trace)", !mgr.IsEnabled && !File.Exists(mgr.PlistPath));
            Check("disable ran `launchctl bootout gui/…`", calls.Exists(c => c.StartsWith("bootout gui/")));

            // (5) IsBundled guard — a dev/dotnet path must REFUSE and install nothing
            var devDir = Path.Combine(Path.GetTempPath(), "nty-launchagent-dev-" + Guid.NewGuid().ToString("N"));
            var dev = new LaunchAgentManager(agentsDir: devDir, programPath: "/usr/local/share/dotnet/dotnet", runctl: FakeCtl);
            Check("IsBundled false for a dev path", !dev.IsBundled);
            Check("dev enable() REFUSED (gated on bundle)", !dev.Enable().ok);
            Check("dev enable wrote NO plist", !dev.IsEnabled && !File.Exists(dev.PlistPath));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }

        Console.WriteLine(failures == 0
            ? "\n=== LAUNCHAGENTTEST PASS ✅ — plist(RunAtLoad+KeepAlive=false, self-targeted) · enable→bootstrap · disable→bootout+REMOVED · dev-path refused ==="
            : $"\n=== LAUNCHAGENTTEST FAIL ❌ ({failures} check(s)) ===");
        return failures == 0 ? 0 : 1;
    }
}

// ── TT-0-C — receive + assert frames from an EXTERNAL student (--recv <mode>) ──────
// The inverse counterparty for MockStudent: the SERVER half of --streamtest/--cameratest/
// --audiotest, but it LISTENS on a fixed port and WAITS for an external student (MockStudent)
// instead of driving an in-process one. Proves MockStudent emits real, decodable frames over the
// wire — i.e. that it's a faithful stand-in. Reuses the same Frame helpers + frame message types +
// structural assertions as the Student-track tests.
static class RecvTest
{
    public static async Task<int> RunAsync(string mode, int port, Guid teacherId)
    {
        int target = mode is "audio" or "camera" ? 8 : 12;
        var listener = new TcpListener(IPAddress.Any, port);   // Any → a real remote student could connect too
        listener.Start();
        Console.WriteLine($"=== MockTeacher --recv {mode} (listening :{port}; waiting for an external student…) ===");

        int received = 0, valid = 0;
        var sessionId = Guid.NewGuid();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));   // deadline: never hang if the student can't capture
        TcpClient conn;
        try { conn = await listener.AcceptTcpClientAsync(cts.Token); }
        catch (OperationCanceledException) { listener.Stop(); Console.WriteLine("\n=== RECV FAIL ❌ — no student connected within 20s ==="); return 1; }
        using var _conn = conn;
        var stream = conn.GetStream();
        Console.WriteLine("  student connected.");

        while (received < target)
        {
            Envelope? env;
            try { env = await Frame.ReadAsync(stream, cts.Token); } catch { break; }
            if (env is null) break;

            if (env.Type == MessageType.Hello)
            {
                var hello = MessagePackSerializer.Deserialize<HelloMessage>(env.Payload);
                Console.WriteLine($"  [Hello] {hello.DisplayName} ({hello.EndpointId.ToString()[..8]}) → sending start ({mode})");
                switch (mode)
                {
                    case "screen":      await Frame.SendAsync(stream, MessageType.StudentStreamStart, Frame.StreamStartPayload(VideoCodec.Mjpeg), teacherId); break;
                    case "screen-h264": await Frame.SendAsync(stream, MessageType.StudentStreamStart, Frame.StreamStartPayload(VideoCodec.H264), teacherId); break;
                    case "camera":      await Frame.SendAsync(stream, MessageType.ConferenceStart, Frame.ConferenceStartPayload(sessionId), teacherId); break;
                    case "audio":       await Frame.SendAsync(stream, MessageType.MicMonitorStart, Array.Empty<byte>(), teacherId); break;
                    default: Console.WriteLine($"  ❌ unknown --recv mode '{mode}'"); listener.Stop(); return 1;
                }
            }
            else if (env.Type == MessageType.Ping)
                await Frame.SendAsync(stream, MessageType.Pong, Array.Empty<byte>(), teacherId);
            else if ((mode is "screen" or "screen-h264") && env.Type == MessageType.StudentStreamFrame)
            {
                var f = MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(env.Payload);
                received++;
                var d = f.FrameData;
                bool ok = mode == "screen-h264"
                    ? d.Length > 4 && d[0] == 0 && d[1] == 0 && d[2] == 0 && d[3] == 1                    // Annex-B start code
                    : d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[^2] == 0xFF && d[^1] == 0xD9;     // JPEG SOI/EOI
                if (ok) valid++;
                if (received <= 3) Console.WriteLine($"  [frame {received}] seq={f.FrameSeq} {f.Width}x{f.Height} {d.Length}B key={f.IsKeyframe} ok={ok}");
            }
            else if (mode == "camera" && env.Type == MessageType.ConferenceCameraFrame)
            {
                var f = MessagePackSerializer.Deserialize<ConferenceCameraFrameMessage>(env.Payload);
                received++;
                var d = f.JpegData;
                bool ok = d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[^2] == 0xFF && d[^1] == 0xD9;
                if (ok) valid++;
                if (received <= 3) Console.WriteLine($"  [cam {received}] {d.Length}B jpeg={ok}");
            }
            else if (mode == "audio" && env.Type == MessageType.StudentAudioStreamFrame)
            {
                var f = MessagePackSerializer.Deserialize<AudioStreamFrameMessage>(env.Payload);
                received++;
                bool ok = f.PcmData.Length == 3200 && f.SampleRate == 16000 && f.Channels == 1 && f.BitsPerSample == 16;
                if (ok) valid++;
                if (received <= 3) Console.WriteLine($"  [pcm {received}] {f.PcmData.Length}B {f.SampleRate}/{f.Channels}/{f.BitsPerSample} ok={ok}");
            }
        }
        listener.Stop();

        bool pass = received >= target && valid == received;
        Console.WriteLine(pass
            ? $"\n=== RECV PASS ✅ — {valid}/{received} valid {mode} frames from the external student (MockStudent = faithful stand-in) ==="
            : $"\n=== RECV FAIL ❌ — {valid}/{received} valid {mode} frames (target {target}); did the student have the capture grant? ===");
        return pass ? 0 : 1;
    }
}

// ── TT-0-D — accept N students + count frames (--recvmany) ─────────────────────────
// The counterparty for MockStudent --classroom N: accept every student that connects, request an
// H.264 stream from each, and count frames — proving the HARNESS delivers N streams. It does NOT
// decode (that's TT-4 against the real Mac Teacher); it just validates delivery + aggregate rate.
static class RecvManyTest
{
    public static async Task<int> RunAsync(int port, int durationSec, Guid teacherId)
    {
        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();
        Console.WriteLine($"=== MockTeacher --recvmany (listening :{port}, {durationSec}s — accept N students, request H.264, count frames) ===");
        int students = 0; long frames = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(durationSec));
        var conns = new List<Task>();

        try
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient conn;
                try { conn = await listener.AcceptTcpClientAsync(cts.Token); }
                catch (OperationCanceledException) { break; }
                Interlocked.Increment(ref students);
                conns.Add(Task.Run(async () =>
                {
                    using var c = conn;
                    var s = c.GetStream();
                    while (!cts.IsCancellationRequested)
                    {
                        Envelope? env;
                        try { env = await Frame.ReadAsync(s, cts.Token); } catch { return; }
                        if (env is null) return;
                        if (env.Type == MessageType.Hello)
                            await Frame.SendAsync(s, MessageType.StudentStreamStart, Frame.StreamStartPayload(VideoCodec.H264), teacherId, cts.Token);
                        else if (env.Type == MessageType.Ping)
                            await Frame.SendAsync(s, MessageType.Pong, Array.Empty<byte>(), teacherId, cts.Token);
                        else if (env.Type == MessageType.StudentStreamFrame)
                            Interlocked.Increment(ref frames);
                    }
                }, cts.Token));
            }
        }
        catch (OperationCanceledException) { }
        listener.Stop();
        try { await Task.WhenAll(conns); } catch { }

        bool ok = students > 0 && frames > 0;
        Console.WriteLine(ok
            ? $"\n=== RECVMANY ✅ — {students} students, {frames} H.264 frames received (~{frames / (double)durationSec:0} fps aggregate) — harness delivered {students} streams ==="
            : $"\n=== RECVMANY ❌ — {students} students, {frames} frames ===");
        return ok ? 0 : 1;
    }
}
