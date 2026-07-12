// Phase 24.2 — Cross-Platform Interop Proof
// ─────────────────────────────────────────────────────────────────────────
// A minimal macOS console app that talks to the SHIPPED Windows Teacher (v1.2)
// over the real wire protocol, using ONLY ClassroomCtrl.Shared.Wire.
//
// It reproduces the shipped Student's transport behavior exactly (verified
// against ClassroomCtrl.Student.Service/ClassroomWorker.cs and
// ClassroomCtrl.Networking/TcpControlServer.cs in the Windows repo):
//
//   Scenario A — Discovery (Windows → Mac decode)
//     The Teacher fire-and-forget-broadcasts a BeaconPayload (map-mode
//     MessagePack) on UDP 7778 every 2 s.  We bind 7778, receive one, and
//     deserialize it.  Success here proves a Mac can decode a Windows-produced
//     payload, and it auto-discovers TeacherIp:TcpPort for Scenario B.
//
//   Scenario B — TCP handshake (bidirectional interop)
//     Connect TCP to Teacher:7777.  Frame = [4-byte big-endian Int32 length]
//     + [MessagePack(Envelope) body].  We send Hello (exactly as the shipped
//     Student does), then Ping.  The Teacher's TcpControlServer auto-replies
//     Pong at the connection level regardless of app state — so a returned
//     Pong is a DETERMINISTIC proof of round-trip wire interop.  Any other
//     envelopes the Teacher sends (e.g. GroupSnapshot) are printed too.
//
// Usage:
//   dotnet run --project tools/CrossPlatformInteropTest -- [options]
//     --teacher-ip <IP>        Skip discovery; connect straight to this IP.
//     --port <n>               TCP control port (default 7777).
//     --channel <id>           Beacon ChannelId to match (default "1234").
//     --discover-timeout <ms>  How long to listen for a beacon (default 8000).
//     --listen-only            Run Scenario A only (no TCP connect).
//     --skip-discovery         Run Scenario B only (requires --teacher-ip).

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ClassroomCtrl.Shared.Discovery;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;

var opts = Options.Parse(args);
Console.WriteLine("=== NTY ClassroomCtrl — Cross-Platform Interop Test (macOS → Windows Teacher) ===");
Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"Options: teacher-ip={opts.TeacherIp ?? "(discover)"} port={opts.Port} channel={opts.Channel} " +
                  $"discover-timeout={opts.DiscoverTimeoutMs}ms listen-only={opts.ListenOnly} skip-discovery={opts.SkipDiscovery}");
Console.WriteLine();

IPEndPoint? target = null;
if (opts.TeacherIp != null && IPAddress.TryParse(opts.TeacherIp, out var forcedIp))
    target = new IPEndPoint(forcedIp, opts.Port);

int failures = 0;

// ── Scenario A ────────────────────────────────────────────────────────────
if (!opts.SkipDiscovery)
{
    Console.WriteLine("── Scenario A: UDP discovery beacon (listening for the Teacher's broadcast) ──");
    var discovered = await TryDiscoverAsync(opts.Channel, opts.DiscoverTimeoutMs);
    if (discovered != null)
    {
        Console.WriteLine($"  ✅ PASS — decoded a Windows-produced beacon; Teacher at {discovered}");
        target ??= discovered; // discovery result only fills target if not forced
    }
    else
    {
        Console.WriteLine("  ⚠️  No matching beacon received within the timeout.");
        Console.WriteLine("     (Teacher not running, different LAN/channel, or firewall blocking UDP 7778.)");
        if (opts.TeacherIp == null) failures++;
    }
    Console.WriteLine();
}

// ── Scenario B ────────────────────────────────────────────────────────────
if (!opts.ListenOnly)
{
    if (target == null)
    {
        Console.WriteLine("── Scenario B skipped: no Teacher endpoint (pass --teacher-ip or let discovery find one). ──");
        failures++;
    }
    else
    {
        Console.WriteLine($"── Scenario B: TCP handshake with Teacher at {target} ──");
        var ok = await TryHandshakeAsync(target);
        if (ok) Console.WriteLine("  ✅ PASS — bidirectional wire interop proven (Hello sent, Pong received).");
        else { Console.WriteLine("  ❌ FAIL — no Pong received / connection error (see log above)."); failures++; }
        Console.WriteLine();
    }
}

Console.WriteLine(failures == 0
    ? "=== RESULT: interop PROVEN ✅ ==="
    : $"=== RESULT: {failures} scenario(s) did not complete ❌ ===");
return failures == 0 ? 0 : 1;


// ── Scenario A implementation ───────────────────────────────────────────────
static async Task<IPEndPoint?> TryDiscoverAsync(string channel, int timeoutMs)
{
    // Mirror TeacherDiscoveryClient: ReuseAddress + non-exclusive bind on 7778.
    using var udp = new UdpClient();
    try
    {
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.ExclusiveAddressUse = false;
    }
    catch { /* best-effort */ }
    udp.EnableBroadcast = true;
    try { udp.Client.Bind(new IPEndPoint(IPAddress.Any, NetworkConstants.DiscoveryUdpPort)); }
    catch (SocketException ex)
    {
        Console.WriteLine($"  ❌ Could not bind UDP {NetworkConstants.DiscoveryUdpPort}: {ex.SocketErrorCode}");
        return null;
    }

    using var cts = new CancellationTokenSource(timeoutMs);
    Console.WriteLine($"  Listening on UDP {NetworkConstants.DiscoveryUdpPort} for up to {timeoutMs} ms …");
    while (!cts.IsCancellationRequested)
    {
        UdpReceiveResult res;
        try { res = await udp.ReceiveAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
        catch (SocketException) { return null; }

        Console.WriteLine($"  RX {res.Buffer.Length} B from {res.RemoteEndPoint}:");
        Console.WriteLine(HexDump(res.Buffer, "     "));
        try
        {
            var beacon = MessagePackSerializer.Deserialize<BeaconPayload>(res.Buffer);
            Console.WriteLine($"     Decoded BeaconPayload: ChannelId=\"{beacon.ChannelId}\" TeacherIp={beacon.TeacherIp} " +
                              $"TcpPort={beacon.TcpPort} ClassName=\"{beacon.ClassName}\" ts={beacon.TimestampUtcMs}");
            if (string.Equals(beacon.ChannelId, channel, StringComparison.OrdinalIgnoreCase)
                && IPAddress.TryParse(beacon.TeacherIp, out var ip))
                return new IPEndPoint(ip, beacon.TcpPort);
            Console.WriteLine($"     (channel mismatch — want \"{channel}\"; still listening)");
        }
        catch (Exception ex) { Console.WriteLine($"     (not a BeaconPayload: {ex.Message})"); }
    }
    return null;
}

// ── Scenario B implementation ───────────────────────────────────────────────
static async Task<bool> TryHandshakeAsync(IPEndPoint ep)
{
    using var client = new TcpClient();
    using var connectCts = new CancellationTokenSource(5000); // mirror Student's 5 s connect bound
    try { await client.ConnectAsync(ep.Address, ep.Port, connectCts.Token); }
    catch (OperationCanceledException) { Console.WriteLine($"  ❌ Connect to {ep} timed out (5 s)."); return false; }
    catch (SocketException ex) { Console.WriteLine($"  ❌ Connect failed: {ex.SocketErrorCode}"); return false; }
    Console.WriteLine($"  TCP connected to {ep}.");

    var stream = client.GetStream();
    var myEndpoint = Guid.NewGuid();

    // 1) Hello — byte-for-byte the shape the shipped Student sends.
    var hello = new HelloMessage
    {
        MachineName = Environment.MachineName,
        DisplayName = $"MacInteropTest ({Environment.MachineName})",
        OsVersion = Environment.OSVersion.VersionString,
        ProtocolVersion = NetworkConstants.ProtocolVersion,
        EndpointId = myEndpoint,
    };
    var helloEnv = Envelope.Create(MessageType.Hello, MessagePackSerializer.Serialize(hello), myEndpoint);
    await SendFrameAsync(stream, helloEnv, "TX Hello");

    // 2) Ping — the transport auto-replies Pong at the connection level.
    var pingEnv = Envelope.Create(MessageType.Ping, Array.Empty<byte>(), myEndpoint);
    await SendFrameAsync(stream, pingEnv, "TX Ping");

    // 3) Read frames until we see a Pong, or time out.
    using var readCts = new CancellationTokenSource(8000);
    var lengthBuf = new byte[4];
    try
    {
        while (!readCts.IsCancellationRequested)
        {
            if (await ReadExactAsync(stream, lengthBuf, 4, readCts.Token) == 0)
            { Console.WriteLine("  Teacher closed the connection."); return false; }
            int len = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
            if (len <= 0 || len > 64 * 1024 * 1024) { Console.WriteLine($"  ❌ Bad frame size {len}."); return false; }
            var body = new byte[len];
            await ReadExactAsync(stream, body, len, readCts.Token);

            var env = Envelope.Deserialize(body);
            Console.WriteLine($"  RX frame {len + 4} B → Envelope Type={env.Type} SenderId={Short(env.SenderId)} " +
                              $"PayloadLen={env.Payload.Length}");
            Console.WriteLine(HexDump(lengthBuf.Concat(body).Take(48).ToArray(), "     "));
            if (env.Type == MessageType.Pong) return true;
            // Anything else (e.g. GroupSnapshot) is informational — keep reading for the Pong.
        }
    }
    catch (OperationCanceledException) { Console.WriteLine("  ❌ Timed out waiting for a Pong (8 s)."); }
    catch (Exception ex) { Console.WriteLine($"  ❌ Read error: {ex.Message}"); }
    return false;
}

static async Task SendFrameAsync(NetworkStream s, Envelope env, string label)
{
    var body = env.Serialize();
    var len = new byte[4];
    BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
    await s.WriteAsync(len);
    await s.WriteAsync(body);
    await s.FlushAsync();
    Console.WriteLine($"  {label}: Type={env.Type} frame={body.Length + 4} B");
    Console.WriteLine(HexDump(len.Concat(body).Take(48).ToArray(), "     "));
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

// ── helpers ─────────────────────────────────────────────────────────────────
static string Short(Guid g) => g.ToString()[..8];

static string HexDump(byte[] data, string indent)
{
    var sb = new System.Text.StringBuilder();
    for (int i = 0; i < data.Length; i += 16)
    {
        var slice = data.Skip(i).Take(16).ToArray();
        var hex = string.Join(' ', slice.Select(b => b.ToString("x2")));
        var ascii = string.Concat(slice.Select(b => b >= 0x20 && b < 0x7f ? (char)b : '.'));
        sb.Append($"{indent}{i:x4}  {hex,-47}  {ascii}");
        if (i + 16 < data.Length) sb.AppendLine();
    }
    if (data.Length == 0) sb.Append($"{indent}(empty)");
    return sb.ToString();
}

// ── options ──────────────────────────────────────────────────────────────────
sealed class Options
{
    public string? TeacherIp;
    public int Port = NetworkConstants.ControlTcpPort;   // 7777
    public string Channel = "1234";
    public int DiscoverTimeoutMs = 8000;
    public bool ListenOnly;
    public bool SkipDiscovery;

    public static Options Parse(string[] a)
    {
        var o = new Options();
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i])
            {
                case "--teacher-ip": o.TeacherIp = Next(a, ref i); break;
                case "--port": o.Port = int.Parse(Next(a, ref i)); break;
                case "--channel": o.Channel = Next(a, ref i); break;
                case "--discover-timeout": o.DiscoverTimeoutMs = int.Parse(Next(a, ref i)); break;
                case "--listen-only": o.ListenOnly = true; break;
                case "--skip-discovery": o.SkipDiscovery = true; break;
                default: Console.WriteLine($"(ignoring unknown arg: {a[i]})"); break;
            }
        }
        return o;
    }

    static string Next(string[] a, ref int i)
        => ++i < a.Length ? a[i] : throw new ArgumentException($"missing value after {a[i - 1]}");
}
