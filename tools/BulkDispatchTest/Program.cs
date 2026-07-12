// Phase 26.1-B — BulkDispatchTest
// ─────────────────────────────────────────────────────────────────────────
// Reproduces the "18/50 locked" bulk-action bug and proves the v1.2.1 fix,
// WITHOUT 50 physical PCs — N loopback TcpClient "students" against a real
// TcpControlServer in one process.
//
// The bug: targeted control ops fan out to every peer (+ receiver-side IsForMe)
// and rode the lossy _outbox (cap 16, DropOldest). A bulk action over N students
// = N broadcast frames; under network back-pressure the cap-16 queue overflows
// and DropOldest silently discards the oldest → only ~the last 16 survive.
//
// Loopback alone is too fast to drop (tiny frames flush instantly), so to
// reproduce the customer's constrained-hotspot behavior we INDUCE back-pressure:
//   • students read slowly (--read-delay ms per frame), and
//   • frames are padded (--pad bytes) so the per-peer socket buffer + cap-16
//     channel can't hold all N at once.
// Under that pressure:
//   • LOSSY   (BroadcastAsync)         → drops at scale  (reproduces ~18/50)
//   • RELIABLE (BroadcastReliableAsync) → FullMode.Wait back-pressures the
//     producer → 0 drops → N/N          (the fix; asserted deterministically)
//
// Usage:
//   dotnet run --project tools/BulkDispatchTest
//   dotnet run --project tools/BulkDispatchTest -- --sizes 2,20,50 --pad 16384 --read-delay 5

using System.Buffers.Binary;
using System.Net.Sockets;
using ClassroomCtrl.Networking;
using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

int[] sizes = { 2, 20, 50 };
int pad = 16 * 1024;      // per-frame payload padding to induce socket back-pressure
int readDelayMs = 5;      // per-frame student read delay (simulates slow/constrained peer)
int basePort = 47771;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--sizes": sizes = args[++i].Split(',').Select(int.Parse).ToArray(); break;
        case "--pad": pad = int.Parse(args[++i]); break;
        case "--read-delay": readDelayMs = int.Parse(args[++i]); break;
        case "--port": basePort = int.Parse(args[++i]); break;
    }
}

Console.WriteLine("=== BulkDispatchTest — reproduce '18/50' + prove the reliable-path fix ===");
Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"pad={pad} B/frame · read-delay={readDelayMs} ms/frame · sizes={string.Join(",", sizes)}");
Console.WriteLine();

int port = basePort;
int failures = 0;

Console.WriteLine($"{"N",4} │ {"LOSSY (before)",-22} │ {"RELIABLE (after)",-22} │ verdict");
Console.WriteLine(new string('─', 66));

foreach (var n in sizes)
{
    int lossy = await RunAsync(n, reliable: false, port++, pad, readDelayMs);
    int reliable = await RunAsync(n, reliable: true, port++, pad, readDelayMs);

    // The FIX assertion: reliable must deliver every targeted frame.
    bool pass = reliable == n;
    if (!pass) failures++;

    string lossyCell = $"{lossy}/{n} locked" + (lossy < n ? " (dropped)" : "");
    string relCell = $"{reliable}/{n} locked" + (reliable == n ? " ✅" : " ❌");
    Console.WriteLine($"{n,4} │ {lossyCell,-22} │ {relCell,-22} │ {(pass ? "PASS" : "FAIL")}");
}

Console.WriteLine(new string('─', 66));
Console.WriteLine(failures == 0
    ? "\n=== RESULT: reliable path delivers N/N at every size — FIX PROVEN ✅ ==="
    : $"\n=== RESULT: {failures} size(s) failed the reliable N/N assertion ❌ ===");
Console.WriteLine("(LOSSY column reproduces the pre-fix drop under induced back-pressure; the "
                + "RELIABLE column is the v1.2.1 guarantee.)");
return failures == 0 ? 0 : 1;


// One run: N loopback students, send N targeted Locks via the chosen path, count
// how many students received their OWN targeted frame (= "locked").
static async Task<int> RunAsync(int n, bool reliable, int port, int pad, int readDelayMs)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var teacherId = Guid.NewGuid();

    var server = new TcpControlServer(NullLogger<TcpControlServer>.Instance, port);
    await server.StartAsync(cts.Token);

    // Each student has an app-level EndpointId; it records which TargetEndpointIds
    // it actually received so we can check whether its own frame survived.
    var studentIds = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();
    var received = new System.Collections.Concurrent.ConcurrentDictionary<Guid, byte>[n];
    var clients = new TcpClient[n];
    var readers = new Task[n];

    for (int i = 0; i < n; i++)
    {
        received[i] = new();
        var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", port, cts.Token);
        clients[i] = client;
        var bag = received[i];
        readers[i] = Task.Run(() => ReadLoopAsync(client, bag, readDelayMs, cts.Token));
    }

    // Wait until the server has accepted all N peers.
    for (int w = 0; w < 200 && server.ConnectedPeers.Count < n; w++)
        await Task.Delay(20, cts.Token);

    // Teacher bursts N targeted Lock frames (fan-out to all peers, per the real
    // code path). Payload padded to induce socket back-pressure.
    var payload = new byte[pad];
    for (int i = 0; i < n; i++)
    {
        var env = Envelope.CreateTargeted(MessageType.LockScreen, payload, teacherId, studentIds[i]);
        if (reliable) await server.BroadcastReliableAsync(env, cts.Token);
        else await server.BroadcastAsync(env, cts.Token);
    }

    // Let students drain: wait until total received stops increasing.
    int lastTotal = -1, stable = 0;
    while (stable < 8 && !cts.IsCancellationRequested)
    {
        await Task.Delay(100, cts.Token);
        int total = received.Sum(r => r.Count);
        if (total == lastTotal) stable++;
        else { stable = 0; lastTotal = total; }
    }

    // "Locked" = students that received a frame targeting their own EndpointId.
    int locked = 0;
    for (int i = 0; i < n; i++)
        if (received[i].ContainsKey(studentIds[i])) locked++;

    for (int i = 0; i < n; i++) clients[i].Dispose();
    server.Dispose();
    return locked;
}

// Loopback student: parse [4B BE len][MessagePack(Envelope)] frames, record each
// TargetEndpointId, sleeping readDelayMs per frame to simulate a slow peer.
static async Task ReadLoopAsync(TcpClient client, System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> seen,
                                int readDelayMs, CancellationToken ct)
{
    var stream = client.GetStream();
    var lenBuf = new byte[4];
    try
    {
        while (!ct.IsCancellationRequested)
        {
            if (await ReadExactAsync(stream, lenBuf, 4, ct) == 0) return;
            int len = BinaryPrimitives.ReadInt32BigEndian(lenBuf);
            if (len <= 0 || len > 64 * 1024 * 1024) return;
            var body = new byte[len];
            await ReadExactAsync(stream, body, len, ct);
            var env = Envelope.Deserialize(body);
            seen.TryAdd(env.TargetEndpointId, 1);
            if (readDelayMs > 0) await Task.Delay(readDelayMs, ct);
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception) { /* connection torn down at end of run */ }
}

static async Task<int> ReadExactAsync(NetworkStream s, byte[] buf, int count, CancellationToken ct)
{
    int read = 0;
    while (read < count)
    {
        int nr = await s.ReadAsync(buf.AsMemory(read, count - read), ct);
        if (nr == 0) return read;
        read += nr;
    }
    return read;
}
