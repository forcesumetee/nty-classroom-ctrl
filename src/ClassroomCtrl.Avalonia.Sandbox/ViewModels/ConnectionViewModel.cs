using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using ClassroomCtrl.Shared.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
        });
        Client.Traffic += (dir, label, size) => Post(() => AddLog(dir, label, size));
        // EnvelopeReceived reflection is wired in Phase 26.0-C.
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
