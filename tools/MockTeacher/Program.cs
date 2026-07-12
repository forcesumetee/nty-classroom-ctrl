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
bool selfTest = false, streamTest = false, streamTestH264 = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port": port = int.Parse(args[++i]); break;
        case "--selftest": selfTest = true; break;
        case "--streamtest": streamTest = true; break;
        case "--streamtest-h264": streamTestH264 = true; break;
    }
}

var teacherId = Guid.NewGuid();

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
