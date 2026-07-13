// TT-1-F — TeacherHost (--serve): the thin LIVE harness for the Teacher track.
// Runs the REAL ClassroomCtrl.Teacher.Core (ControlServer + StudentRoster) on
// 0.0.0.0:7777 and prints the roster live, so a real student — MockStudent or a
// shipped, unmodified Windows Student — can connect over the LAN and appear in
// the roster. This is the TT-1 LIVE gate. It is NOT the Teacher app: no UI, no
// Avalonia, no decode, no bulk ops (that's TT-6+). Just "server runs, roster prints".
//
// Usage:
//   dotnet run --project tools/TeacherHost -- --serve            # binds 0.0.0.0:7777
//   dotnet run --project tools/TeacherHost -- --serve --port 7777
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Teacher.Core;
using ClassroomCtrl.Teacher.Services;
using Microsoft.Extensions.Logging;

int port = NetworkConstants.ControlTcpPort;   // 7777
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--serve": break;                                   // default action; accepted for readability
        case "--port": port = int.Parse(args[++i]); break;
    }
}

var factory = new ConsoleLoggerFactory();

// The REAL server + roster. bindAddress defaults to IPAddress.Any → all interfaces
// (LAN-reachable). staleAfterMs defaults to the shipped 15 s. This is production wiring.
var server = new ControlServer(factory.CreateLogger<ControlServer>(), factory, IPAddress.Any, port);
var roster = new StudentRoster(server);

roster.StudentAdded += (_, e) =>
    Console.WriteLine($"  ＋ JOIN   {e.DisplayName}  [{e.MachineName}]  {Short(e.EndpointId)}   → roster: {roster.Count}");
roster.StudentRemoved += (_, id) =>
    Console.WriteLine($"  － LEAVE  {Short(id)}   → roster: {roster.Count}");

Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║  TeacherHost — real ClassroomCtrl.Teacher.Core  (TT-1-F LIVE)  ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
Console.WriteLine($"  Listening on 0.0.0.0:{port}  (all interfaces)");
var ips = LanIPv4().ToList();
if (ips.Count > 0)
{
    Console.WriteLine("  Point a student at one of these LAN addresses:");
    foreach (var ip in ips) Console.WriteLine($"      {ip}:{port}");
}
Console.WriteLine("  Liveness: a student that stays listed is alive (heartbeat every 5 s;");
Console.WriteLine("  the 15 s stale-sweep reaps a silent one → LEAVE). Ctrl-C to stop.\n");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, ev) => { ev.Cancel = true; cts.Cancel(); };

await server.StartAsync(cts.Token);

try
{
    // Periodic liveness snapshot — students that persist across snapshots are alive.
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(5000, cts.Token);
        var names = roster.Students.Select(s => s.DisplayName).ToList();
        Console.WriteLine(names.Count == 0
            ? "  · roster: (empty)"
            : $"  · roster: {names.Count} connected — {string.Join(", ", names)}");
    }
}
catch (OperationCanceledException) { }
finally
{
    Console.WriteLine($"\n  stopping — disposing server (releasing :{port}) …");
    server.Dispose();                       // _cts.Cancel() + _listener.Stop() → socket released
    Console.WriteLine("  stopped. socket released.");
}
return 0;

static string Short(Guid g) => g.ToString()[..8];

static IEnumerable<string> LanIPv4()
{
    foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (ni.OperationalStatus != OperationalStatus.Up) continue;
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
        foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            if (ua.Address.AddressFamily == AddressFamily.InterNetwork)
                yield return ua.Address.ToString();
    }
}

// ── tiny console ILogger (no logging package needed → offline-safe) ─────────────
sealed class ConsoleLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}

sealed class ConsoleLogger : ILogger
{
    private readonly string _cat;
    public ConsoleLogger(string cat) { int dot = cat.LastIndexOf('.'); _cat = dot >= 0 ? cat[(dot + 1)..] : cat; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;   // suppress Debug spam
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
    {
        if (!IsEnabled(level)) return;
        string tag = level switch { LogLevel.Warning => "WARN", LogLevel.Error or LogLevel.Critical => "ERR ", _ => "info" };
        Console.WriteLine($"    [{tag}] {_cat}: {fmt(state, ex)}");
        if (ex != null) Console.WriteLine($"           {ex.GetType().Name}: {ex.Message}");
    }
}
