using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Phase 28-C — drives the Camera tab: camera permission, device enumeration +
/// selection, and a live JPEG self-preview from the native AVCaptureSession helper.
///
/// Threading mirrors <see cref="ScreenCaptureViewModel"/>: frames arrive on the
/// native delivery thread; the newest is coalesced and decoded on the UI thread
/// (a 320×240 JPEG decode is cheap). Independent of the screen path.
/// </summary>
public partial class CameraCaptureViewModel : ObservableObject
{
    // Shipped peer-cam format (matches the Windows student: 320×240, ~10 fps, Q70).
    private const int CamWidth = 320, CamHeight = 240, CamFps = 10, CamQuality = 70;

    private readonly CameraCaptureService _camera = new();

    // ── permission ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string permissionStatus = "Unknown";
    [ObservableProperty] private bool isGranted;
    [ObservableProperty] private string hint = "Camera permission is required to preview and stream the webcam.";

    public string StatusClass => IsGranted ? "granted" : "denied";

    // ── devices ─────────────────────────────────────────────────────────────────
    public ObservableCollection<CameraDevice> Devices { get; } = new();
    [ObservableProperty] private CameraDevice? selectedDevice;

    // ── capture ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool isCapturing;
    [ObservableProperty] private Bitmap? frame;
    [ObservableProperty] private string resolutionText = "—";
    [ObservableProperty] private string fpsText = "0.0";
    [ObservableProperty] private long framesText;

    // Coalescing (guarded by _gate).
    private readonly object _gate = new();
    private byte[]? _pending;
    private int _pw, _ph;
    private bool _renderQueued;

    // fps window
    private int _fpsFrames;
    private DateTime _fpsStart = DateTime.UtcNow;

    public CameraCaptureViewModel()
    {
        if (!CameraCaptureService.IsSupported)
        {
            PermissionStatus = "Unsupported (macOS only)";
            Hint = "The native camera helper is macOS-only; run on macOS to test.";
            return;
        }
        _camera.JpegFrameReceived += OnJpegReceived;
        Refresh(_camera.CheckPermission());
        _ = RefreshDevices();
    }

    // ── permission commands ─────────────────────────────────────────────────────
    [RelayCommand]
    private void CheckPermission() => Refresh(_camera.CheckPermission());

    [RelayCommand]
    private async Task RequestPermission()
    {
        var result = await _camera.RequestPermissionAsync();
        Refresh(result);
        Hint = result == CameraPermission.Granted
            ? "Camera granted. Pick a device and press Start capture."
            : "Camera was denied. Enable it in System Settings › Privacy & Security › Camera, then Check.";
        await RefreshDevices();
    }

    // ── device commands ─────────────────────────────────────────────────────────
    [RelayCommand]
    private async Task RefreshDevices()
    {
        var devices = await _camera.EnumerateDevicesAsync();
        var previous = SelectedDevice?.Index ?? 0;
        Devices.Clear();
        foreach (var d in devices) Devices.Add(d);
        SelectedDevice = Devices.Count > 0
            ? (previous < Devices.Count ? Devices[previous] : Devices[0])
            : null;
        if (Devices.Count == 0) Hint = "No camera found. Connect a webcam and Refresh.";
    }

    // ── capture commands ────────────────────────────────────────────────────────
    private bool CanStart() => IsGranted && !IsCapturing && SelectedDevice != null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartCapture()
    {
        _fpsFrames = 0; _fpsStart = DateTime.UtcNow;
        int idx = SelectedDevice?.Index ?? 0;
        int rc = await _camera.StartAsync(idx, CamWidth, CamHeight, CamFps, CamQuality);
        if (rc == 0)
        {
            IsCapturing = true;
            Hint = $"Capturing {SelectedDevice?.Name} at ~{CamFps} fps ({CamWidth}×{CamHeight}).";
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
        await _camera.StopAsync();
        IsCapturing = false;
        Hint = "Stopped.";
        StartCaptureCommand.NotifyCanExecuteChanged();
        StopCaptureCommand.NotifyCanExecuteChanged();
    }

    // ── frame path ──────────────────────────────────────────────────────────────
    private void OnJpegReceived(byte[] jpeg, int w, int h) // native delivery thread
    {
        bool schedule = false;
        lock (_gate)
        {
            _pending = jpeg; _pw = w; _ph = h;             // keep only the newest
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

        try
        {
            using var ms = new MemoryStream(buf);
            var bmp = new Bitmap(ms);          // decode JPEG → displayable bitmap
            Frame?.Dispose();
            Frame = bmp;
        }
        catch { /* transient decode: next frame lands ~100 ms later */ return; }

        ResolutionText = $"{w} × {h}";
        FramesText = _camera.Stats().Frames;
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

    partial void OnSelectedDeviceChanged(CameraDevice? value) => StartCaptureCommand.NotifyCanExecuteChanged();

    private void Refresh(CameraPermission p)
    {
        IsGranted = p == CameraPermission.Granted;
        PermissionStatus = p switch
        {
            CameraPermission.Granted => "Granted",
            CameraPermission.NotDetermined => "Not requested",
            _ => "Not granted",
        };
        OnPropertyChanged(nameof(StatusClass));
        StartCaptureCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeError(int rc) => rc switch
    {
        -2 => "bad device index",
        -3 => "already running",
        -4 => "null callback",
        -5 => "could not add camera input (in use / permission)",
        -6 => "could not add video output",
        _ => "native error",
    };
}
