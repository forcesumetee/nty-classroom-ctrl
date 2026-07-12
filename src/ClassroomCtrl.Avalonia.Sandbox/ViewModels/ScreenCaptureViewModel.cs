using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Phase 27-A — drives the Screen Capture tab. 27-A-2: permission. 27-A-3: live
/// SCStream frames → WriteableBitmap + fps/resolution/dropped stats.
///
/// Threading: <see cref="ScreenCaptureService.FrameReceived"/> fires on the native
/// delivery thread. Frames are coalesced (only the newest is rendered; older ones
/// waiting for the UI thread count as "dropped") and the render runs on the UI
/// thread via <see cref="Dispatcher.UIThread"/> (§18 pattern).
/// </summary>
public partial class ScreenCaptureViewModel : ObservableObject
{
    private const int TargetFps = 12;
    private readonly ScreenCaptureService _capture = new();

    // ── permission (27-A-2) ─────────────────────────────────────────────────────
    [ObservableProperty] private string permissionStatus = "Unknown";
    [ObservableProperty] private bool isGranted;
    [ObservableProperty] private string hint = "Screen Recording permission is required to capture the display.";

    public string StatusClass => IsGranted ? "granted" : "denied";

    // ── capture (27-A-3) ────────────────────────────────────────────────────────
    [ObservableProperty] private bool isCapturing;
    [ObservableProperty] private WriteableBitmap? frame;
    [ObservableProperty] private string resolutionText = "—";
    [ObservableProperty] private string fpsText = "0.0";
    [ObservableProperty] private int droppedFrames;

    // Coalescing state (guarded by _gate).
    private readonly object _gate = new();
    private byte[]? _pending;
    private int _pw, _ph;
    private bool _renderQueued;
    private long _dropped;

    // fps window
    private int _fpsFrames;
    private DateTime _fpsStart = DateTime.UtcNow;

    public ScreenCaptureViewModel()
    {
        if (!ScreenCaptureService.IsSupported)
        {
            PermissionStatus = "Unsupported (macOS only)";
            Hint = "The native capture helper is macOS-only; run on macOS to test.";
            return;
        }
        _capture.FrameReceived += OnFrameReceived;
        Refresh(_capture.CheckPermission());
    }

    // ── permission commands ─────────────────────────────────────────────────────
    [RelayCommand]
    private void CheckPermission() => Refresh(_capture.CheckPermission());

    [RelayCommand]
    private async Task RequestPermission()
    {
        var result = await _capture.RequestPermissionAsync();
        Refresh(result);
        Hint = result == ScreenPermission.Granted
            ? "Screen Recording granted. Press Start capture."
            : "If the prompt appeared: grant Screen Recording in System Settings › Privacy & "
              + "Security, then RELAUNCH the app (the grant only takes effect on next launch). Then Check.";
    }

    // ── capture commands ────────────────────────────────────────────────────────
    private bool CanStart() => IsGranted && !IsCapturing;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartCapture()
    {
        DroppedFrames = 0; _dropped = 0;
        _fpsFrames = 0; _fpsStart = DateTime.UtcNow;
        int rc = await _capture.StartAsync(TargetFps);
        if (rc == 0)
        {
            IsCapturing = true;
            Hint = $"Capturing main display at ~{TargetFps} fps.";
        }
        else
        {
            Hint = $"Start failed (code {rc}: {DescribeError(rc)}).";
        }
        StartCaptureCommand.NotifyCanExecuteChanged();
        StopCaptureCommand.NotifyCanExecuteChanged();
    }

    private bool CanStop() => IsCapturing;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopCapture()
    {
        await _capture.StopAsync();
        IsCapturing = false;
        Hint = "Stopped.";
        StartCaptureCommand.NotifyCanExecuteChanged();
        StopCaptureCommand.NotifyCanExecuteChanged();
    }

    // ── frame path ──────────────────────────────────────────────────────────────
    private void OnFrameReceived(byte[] bgra, int w, int h) // native delivery thread
    {
        bool schedule = false;
        lock (_gate)
        {
            if (_pending != null) Interlocked.Increment(ref _dropped); // previous not yet rendered
            _pending = bgra; _pw = w; _ph = h;
            if (!_renderQueued) { _renderQueued = true; schedule = true; }
        }
        if (schedule) Dispatcher.UIThread.Post(RenderPending);
    }

    private void RenderPending() // UI thread
    {
        byte[] buf; int w, h;
        lock (_gate)
        {
            if (_pending == null) { _renderQueued = false; return; }
            buf = _pending; w = _pw; h = _ph;
            _pending = null; _renderQueued = false;
        }

        // Recreate the bitmap when size changes; otherwise reuse + copy in place.
        var wb = Frame;
        if (wb == null || wb.PixelSize.Width != w || wb.PixelSize.Height != h)
            wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

        using (var fb = wb.Lock())
            Marshal.Copy(buf, 0, fb.Address, buf.Length);

        // Reassign to force the Image to redraw (Avalonia observes the Source ref).
        Frame = null;
        Frame = wb;

        ResolutionText = $"{w} × {h}";
        DroppedFrames = (int)Interlocked.Read(ref _dropped);
        TickFps();
    }

    private void TickFps()
    {
        _fpsFrames++;
        var elapsed = (DateTime.UtcNow - _fpsStart).TotalSeconds;
        if (elapsed >= 1.0)
        {
            FpsText = (_fpsFrames / elapsed).ToString("0.0");
            _fpsFrames = 0;
            _fpsStart = DateTime.UtcNow;
        }
    }

    private void Refresh(ScreenPermission p)
    {
        IsGranted = p == ScreenPermission.Granted;
        PermissionStatus = IsGranted ? "Granted" : "Not granted";
        OnPropertyChanged(nameof(StatusClass));
        StartCaptureCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeError(int rc) => rc switch
    {
        -1 => "not permitted — grant Screen Recording + relaunch",
        -2 => "no display found",
        -3 => "already running",
        -5 => "content query timed out",
        -8 => "stream failed to start",
        _ => "native error",
    };
}
