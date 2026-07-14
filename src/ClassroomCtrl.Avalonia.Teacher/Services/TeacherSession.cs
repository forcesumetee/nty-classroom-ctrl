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
public sealed class TeacherSession : IDisposable, IStudentStreamSource, IStudentCommandSink, ITeacherMessaging, ITeacherScreenSink
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

    // ─────── TT-3-B: IStudentStreamSource — the screen-view seam ───────
    // Pure re-exposure of the already-ported ControlServer frame flow (TT-1-C) so a
    // ScreenViewModel depends on the interface, not the concrete server.

    public event EventHandler<(Guid StudentId, ScreenStreamFrameMessage Frame)>? StudentStreamFrameReceived
    {
        add => _server.StudentStreamFrameReceived += value;
        remove => _server.StudentStreamFrameReceived -= value;
    }

    public Task RequestStudentStreamAsync(Guid studentId, VideoCodec codec, CancellationToken ct)
        => _server.RequestStudentStreamAsync(studentId, codec, ct);

    public Task StopStudentStreamAsync(Guid studentId, CancellationToken ct)
        => _server.StopStudentStreamAsync(studentId, ct);

    // ─────── TT-5-B: IStudentCommandSink — the per-student command seam ───────
    // Pure re-exposure of the already-ported ControlServer command methods (TT-1-C),
    // forwarding the reliable flag so the controller's reliable:true reaches the
    // never-drop channel. Same passthrough shape as IStudentStreamSource above.

    public Task LockAsync(Guid endpointId, bool locked, bool reliable, CancellationToken ct)
        => _server.LockOneAsync(endpointId, locked, ct, reliable);

    public Task PowerAsync(Guid endpointId, MessageType type, bool reliable, CancellationToken ct)
        => _server.PowerOneAsync(endpointId, type, ct, reliable);

    // ─────── TT-7-B: ITeacherMessaging — chat / hand-raise / reaction seam ───────
    // Same passthrough shape as the two seams above. The DM send bakes in reliable:true
    // (the seam guarantees a DM never rides the DropOldest queue — TT-5-A discipline).

    public event EventHandler<ChatMessage>? ChatReceived
    {
        add => _server.ChatReceived += value;
        remove => _server.ChatReceived -= value;
    }

    public event EventHandler<HandRaiseMessage>? HandRaiseReceived
    {
        add => _server.HandRaiseReceived += value;
        remove => _server.HandRaiseReceived -= value;
    }

    public event EventHandler<(Guid SenderId, ReactionMessage Msg)>? ReactionReceived
    {
        add => _server.ReactionReceived += value;
        remove => _server.ReactionReceived -= value;
    }

    public Task BroadcastChatAsync(string text, CancellationToken ct)
        => _server.BroadcastChatAsync(text, ct);

    public Task SendDirectMessageAsync(Guid endpointId, string text, CancellationToken ct)
        => _server.SendDirectMessageAsync(endpointId, text, ct, reliable: true);

    public Task BroadcastReactionAsync(ReactionMessage msg, CancellationToken ct)
        => _server.BroadcastReactionAsync(msg, ct);

    public Task SendHandLowerAsync(Guid studentId, CancellationToken ct)
        => _server.SendHandLowerAsync(studentId, ct);

    // ─────── TT-8-C: ITeacherScreenSink — teacher "Share My Screen" broadcast ───────
    // Passthrough to the already-ported ControlServer sends (frames lossy; STOP reliable — bug #5).

    public Task BroadcastScreenStreamControlAsync(bool start, CancellationToken ct)
        => _server.BroadcastScreenStreamControlAsync(start, ct);

    public Task BroadcastScreenFrameAsync(ScreenStreamFrameMessage frame, CancellationToken ct)
        => _server.BroadcastScreenFrameAsync(frame, ct);

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
