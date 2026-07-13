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
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--selftest": selfTest = true; break;
        case "--ip": ip = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--name": name = args[++i]; break;
    }
}

return selfTest ? await SelfTest.RunAsync() : await Interactive.RunAsync(ip, port, name);


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
        Console.WriteLine($"=== MockStudent → {ip}:{port} as \"{name}\" (Ctrl+C to quit) ===");
        var client = new WireClient();
        client.StatusChanged += s => Console.WriteLine($"  [status] {s}");
        client.Traffic += (dir, label, size) => { if (dir != WireDirection.Rx) Console.WriteLine($"  [{dir}] {label} {size}B"); };
        client.EnvelopeReceived += env => Console.WriteLine($"  [rx] received {Dispatch.Describe(env)}");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { await client.RunAsync(ip, port, name, cts.Token); }
        catch (OperationCanceledException) { }
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
