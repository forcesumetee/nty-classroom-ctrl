using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using ClassroomCtrl.Shared.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MessagePack;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>One row in the wire-traffic debug log.</summary>
public sealed class WireLogRow
{
    public required string Time { get; init; }
    public required string Direction { get; init; }   // "TX" / "RX" / "SYS"
    public required string Glyph { get; init; }        // ▲ / ▼ / •
    public required string Label { get; init; }
    public required string Size { get; init; }
    /// <summary>Decoded, human-readable summary (filled in Phase 26.0-C dispatch).</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// Phase 26.0 — VM behind the Connection tab. Owns a single <see cref="WireClient"/>
/// (stable EndpointId across reconnects → one persistent tile on the Teacher) and
/// marshals its background-thread events onto the UI thread via
/// <see cref="Dispatcher.UIThread"/>. The Connect flow reuses the Phase 25.7-C IP
/// validation (now <see cref="IpValidation"/>).
/// </summary>
public partial class ConnectionViewModel : ObservableObject
{
    private const int MaxLogRows = 500;

    public WireClient Client { get; } = new();

    /// <summary>Reflects the state Teacher commands drive onto this student.</summary>
    public StudentSelfTileViewModel SelfTile { get; } = new();

    /// <summary>Phase 27-C — screen → JPEG → StudentStreamFrame streamer.</summary>
    private readonly ScreenStreamer _streamer = new();

    private CancellationTokenSource? _cts;

    [ObservableProperty] private string teacherIp = "172.20.10.7";  // Phase 24.2 hotspot subnet
    [ObservableProperty] private string portText = "7777";
    [ObservableProperty] private string displayName = $"Mac Sandbox ({Environment.MachineName})";
    [ObservableProperty] private string errorMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsFullyConnected))]
    [NotifyPropertyChangedFor(nameof(IsConnectingState))]
    [NotifyPropertyChangedFor(nameof(StatusClass))]
    private WireStatus status = WireStatus.Disconnected;

    public ObservableCollection<WireLogRow> Log { get; } = new();

    public string StatusText => Status switch
    {
        WireStatus.Connected => "Connected",
        WireStatus.Connecting => "Connecting…",
        WireStatus.Reconnecting => "Reconnecting…",
        _ => "Disconnected",
    };

    /// <summary>Style-class hook for the status pill.</summary>
    public string StatusClass => Status switch
    {
        WireStatus.Connected => "connected",
        WireStatus.Connecting or WireStatus.Reconnecting => "connecting",
        _ => "disconnected",
    };

    public bool IsDisconnected => Status == WireStatus.Disconnected;
    public bool IsConnected => Status != WireStatus.Disconnected;
    public bool IsFullyConnected => Status == WireStatus.Connected;
    public bool IsConnectingState => Status is WireStatus.Connecting or WireStatus.Reconnecting;

    public ConnectionViewModel()
    {
        // WireClient raises on background threads → marshal to the UI thread.
        Client.StatusChanged += s => Post(() =>
        {
            Status = s;
            SelfTile.IsOnline = s == WireStatus.Connected;
            SelfTile.DisplayName = DisplayName;
            if (s == WireStatus.Disconnected) { _ = _streamer.StopAsync(); SelfTile.Reset(); }
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            RaiseHandCommand.NotifyCanExecuteChanged();
        });
        Client.Traffic += (dir, label, size) => Post(() => AddLog(dir, label, size));
        Client.EnvelopeReceived += env => Post(() => Dispatch(env));
        _streamer.FrameSent += seq => Post(() => SelfTile.StreamedFrames = seq);
    }

    /// <summary>Decode the requested codec from a StudentStreamStartRequest; default
    /// MJPEG if the payload is absent/unreadable (older Teacher builds send empty).</summary>
    private static VideoCodec DecodeStreamCodec(byte[] payload)
    {
        try
        {
            if (payload.Length == 0) return VideoCodec.Mjpeg;
            return MessagePackSerializer.Deserialize<StudentStreamStartRequest>(payload).Codec;
        }
        catch { return VideoCodec.Mjpeg; }
    }

    private async Task StartStreamingAsync(VideoCodec codec)
    {
        int rc = await _streamer.StartAsync(Client, codec);
        Post(() =>
        {
            SelfTile.IsStreaming = rc == 0;
            SelfTile.StreamCodec = codec == VideoCodec.H264 ? "H.264" : "MJPEG";
            if (rc == 0) SelfTile.LastDeferred = "";
            else SelfTile.LastDeferred = $"StudentStreamStart · capture failed (code {rc})";
            AddLog(WireDirection.System, rc == 0 ? $"screen streaming started ({codec})" : $"stream start failed ({rc})", 0);
        });
    }

    private async Task StopStreamingAsync()
    {
        await _streamer.StopAsync();
        Post(() =>
        {
            SelfTile.IsStreaming = false;
            AddLog(WireDirection.System, "screen streaming stopped", 0);
        });
    }

    /// <summary>Decode an inbound envelope: enrich the traffic log with a summary
    /// and reflect the command onto <see cref="SelfTile"/>. No enforcement.
    /// Public so HeadlessCapture can seed a representative state deterministically.</summary>
    public void Dispatch(Envelope env)
    {
        string detail;
        switch (env.Type)
        {
            case MessageType.Pong:
                detail = "keepalive ack";
                break;
            case MessageType.LockScreen:
                SelfTile.IsLocked = true;
                detail = "🔒 lock screen";
                break;
            case MessageType.UnlockScreen:
                SelfTile.IsLocked = false;
                detail = "🔓 unlock screen";
                break;
            case MessageType.PolicyApply:
                detail = ApplyPolicy(env.Payload);
                break;
            case MessageType.PolicyRevert:
                SelfTile.SetPolicy(Array.Empty<string>());
                detail = "revert policy";
                break;
            case MessageType.ChatBroadcast:
            case MessageType.ChatDirect:
            case MessageType.ChatRoom:
                detail = ApplyChat(env.Payload);
                break;
            case MessageType.HandRaise:
                SelfTile.IsHandRaised = true;
                detail = "hand raised";
                break;
            case MessageType.HandLower:
                SelfTile.IsHandRaised = false;
                detail = "hand lowered";
                break;
            // Phase 27-C — the Teacher's "View Screen" request. Start real
            // ScreenCaptureKit → JPEG → StudentStreamFrame streaming.
            case MessageType.StudentStreamStart:
                var codec = DecodeStreamCodec(env.Payload);
                _ = StartStreamingAsync(codec);
                detail = $"▶ streaming screen to teacher ({codec})";
                break;
            case MessageType.StudentStreamStop:
                _ = StopStreamingAsync();
                detail = "■ stream stopped";
                break;
            // Remaining capture-class commands: logged + noted, deferred until their
            // native macOS APIs land (Phase 27-B+). No frames produced.
            case MessageType.RequestScreenshot:
            case MessageType.ScreenStreamStart:
            case MessageType.CameraStart:
            case MessageType.ConferenceStart:
            case MessageType.MicMonitorStart:
                detail = "deferred — needs native capture (Phase 27+)";
                SelfTile.LastDeferred = $"{env.Type} · deferred (needs native APIs)";
                break;
            default:
                detail = "";
                break;
        }
        AddLog(WireDirection.Rx, env.Type.ToString(), Client.LastRxSize, detail);
    }

    private string ApplyPolicy(byte[] payload)
    {
        try
        {
            var p = MessagePackSerializer.Deserialize<PolicyApplyMessage>(payload);
            var chips = new List<string>();
            if (p.BlockUsbStorage) chips.Add("USB");
            if (p.BlockOpticalDrive) chips.Add("CD/DVD");
            if (p.BlockPrinting) chips.Add("Print");
            if (p.BlockedProcessNames.Count > 0) chips.Add($"{p.BlockedProcessNames.Count} apps");
            if (p.BlockedHostnames.Count > 0) chips.Add($"{p.BlockedHostnames.Count} sites");
            SelfTile.SetPolicy(chips);
            return chips.Count == 0 ? "apply policy (none)" : "apply policy: " + string.Join(", ", chips);
        }
        catch (Exception ex) { return $"policy decode failed: {ex.Message}"; }
    }

    private string ApplyChat(byte[] payload)
    {
        try
        {
            var c = MessagePackSerializer.Deserialize<ClassroomCtrl.Shared.Protocol.ChatMessage>(payload);
            SelfTile.LastChat = $"{c.SenderName}: {c.Text}";
            return $"💬 {c.SenderName}: {c.Text}";
        }
        catch (Exception ex) { return $"chat decode failed: {ex.Message}"; }
    }

    private bool CanConnect() => Status == WireStatus.Disconnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        ErrorMessage = "";
        if (!IpValidation.IsValidIpv4(TeacherIp))
        {
            ErrorMessage = "Enter a valid teacher IPv4 address (e.g. 172.20.10.7)";
            return;
        }
        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1 or > 65535)
        {
            ErrorMessage = "Port must be 1–65535";
            return;
        }

        _cts = new CancellationTokenSource();
        // Fire-and-forget: RunAsync only returns when Disconnect cancels it.
        _ = Client.RunAsync(TeacherIp.Trim(), port, DisplayName.Trim(), _cts.Token);
    }

    private bool CanDisconnect() => Status != WireStatus.Disconnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect() => _cts?.Cancel();

    private bool CanRaiseHand() => Status == WireStatus.Connected;

    /// <summary>Bonus S→T: toggle + send a HandRaise so the Teacher tile lights up.</summary>
    [RelayCommand(CanExecute = nameof(CanRaiseHand))]
    private async Task RaiseHand()
    {
        var raise = !SelfTile.IsHandRaised;
        SelfTile.IsHandRaised = raise;
        var msg = new HandRaiseMessage
        {
            StudentId = Client.EndpointId,
            StudentName = DisplayName,
            IsRaised = raise,
        };
        try
        {
            await Client.SendAsync(raise ? MessageType.HandRaise : MessageType.HandLower,
                                   MessagePackSerializer.Serialize(msg), CancellationToken.None);
        }
        catch (Exception ex) { AddLog(WireDirection.System, $"hand-raise send failed: {ex.Message}", 0); }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    /// <summary>Append a traffic row (already on the UI thread). Detail/summary for
    /// RX frames is enriched by the dispatcher in Phase 26.0-C.</summary>
    public void AddLog(WireDirection dir, string label, int size, string detail = "")
    {
        var (name, glyph) = dir switch
        {
            WireDirection.Tx => ("TX", "▲"),
            WireDirection.Rx => ("RX", "▼"),
            _ => ("SYS", "•"),
        };
        Log.Add(new WireLogRow
        {
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            Direction = name,
            Glyph = glyph,
            Label = label,
            Size = size > 0 ? $"{size} B" : "",
            Detail = detail,
        });
        while (Log.Count > MaxLogRows) Log.RemoveAt(0);
    }

    private static void Post(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}
