using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Teacher.Core;
using ClassroomCtrl.Teacher.Services;
using Microsoft.Extensions.Logging;   // for the generic CreateLogger<T>() extension

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-2-D — the app's backend session: the REAL Teacher.Core server + roster +
/// grid VM, wired together with guaranteed teardown. App.axaml.cs owns one and
/// the window binds <c>MainWindowViewModel(session.Grid, session.ListenAddress)</c>.
/// Extracted (not inlined in App) so the socket-teardown is unit-testable — the
/// same "never leave :7777 bound" discipline as TeacherHost / the TT-1 self-tests.
/// </summary>
public sealed class TeacherSession : IDisposable
{
    private readonly ControlServer _server;
    private bool _disposed;

    public StudentRoster Roster { get; }
    public TeacherGridViewModel Grid { get; }

    public TeacherSession(int port = NetworkConstants.ControlTcpPort, IPAddress? bindAddress = null)
    {
        var f = new ConsoleLoggerFactory();
        // bindAddress defaults to IPAddress.Any (0.0.0.0 → LAN-reachable) for
        // production; the socket-teardown test passes IPAddress.Loopback + port 0.
        _server = new ControlServer(f.CreateLogger<ControlServer>(), f, IPAddress.Any, port, bindAddress);
        Roster = new StudentRoster(_server);
        Grid = new TeacherGridViewModel(Roster);
    }

    public Task StartAsync() => _server.StartAsync(CancellationToken.None);

    public int? BoundPort => _server.BoundPort;

    public string ListenAddress
    {
        get
        {
            var port = BoundPort;
            var ips = LanIPv4().ToList();
            var where = ips.Count > 0 ? string.Join(", ", ips.Select(ip => $"{ip}:{port}")) : $"0.0.0.0:{port}";
            return $"Listening — point a student at {where}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _server.Dispose();   // _cts.Cancel() + _listener.Stop() → socket released
    }

    private static IEnumerable<string> LanIPv4()
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
}
