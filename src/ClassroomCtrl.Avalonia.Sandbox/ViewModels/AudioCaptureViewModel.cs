using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Phase 29-D — drives the Audio tab: mic permission + a live RMS level meter from the
/// native AVAudioEngine helper. NO loopback playback (would cause acoustic feedback);
/// the meter is the proof that capture works. Independent of screen + camera.
///
/// Threading: PCM frames arrive on the native real-time thread ~10×/s; each updates the
/// meter (from the native RMS) on the UI thread. A frame's bytes aren't rendered — only
/// its level — so no coalescing is needed.
/// </summary>
public partial class AudioCaptureViewModel : ObservableObject
{
    private const int SampleRate = 16000, Channels = 1;  // shipped wire format

    private readonly AudioCaptureService _audio = new();

    // ── permission ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string permissionStatus = "Unknown";
    [ObservableProperty] private bool isGranted;
    [ObservableProperty] private string hint = "Microphone permission is required to capture and stream audio.";

    public string StatusClass => IsGranted ? "granted" : "denied";

    // ── capture ─────────────────────────────────────────────────────────────────
    [ObservableProperty] private bool isCapturing;
    [ObservableProperty] private int rmsLevel;        // 0..100, bound to the meter
    [ObservableProperty] private long framesCounter;
    [ObservableProperty] private string formatText = "—";

    public AudioCaptureViewModel()
    {
        if (!AudioCaptureService.IsSupported)
        {
            PermissionStatus = "Unsupported (macOS only)";
            Hint = "The native mic helper is macOS-only; run on macOS to test.";
            return;
        }
        _audio.PcmFrameReceived += OnPcmReceived;
        Refresh(_audio.CheckPermission());
    }

    // ── permission commands ─────────────────────────────────────────────────────
    [RelayCommand]
    private void CheckPermission() => Refresh(_audio.CheckPermission());

    [RelayCommand]
    private async Task RequestPermission()
    {
        var result = await _audio.RequestPermissionAsync();
        Refresh(result);
        Hint = result == AudioPermission.Granted
            ? "Microphone granted. Press Start capture and speak — the meter should move."
            : "Microphone was denied. Enable it in System Settings › Privacy & Security › Microphone, then Check.";
    }

    // ── capture commands ────────────────────────────────────────────────────────
    private bool CanStart() => IsGranted && !IsCapturing;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartCapture()
    {
        int rc = await _audio.StartAsync(SampleRate, Channels);
        if (rc == 0)
        {
            IsCapturing = true;
            FormatText = $"{SampleRate} Hz · {(Channels == 1 ? "mono" : "stereo")} · 16-bit";
            Hint = "Capturing — speak into the mic; the level meter should react (no loopback, so you won't hear yourself).";
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
        await _audio.StopAsync();
        IsCapturing = false;
        RmsLevel = 0;
        Hint = "Stopped.";
        StartCaptureCommand.NotifyCanExecuteChanged();
        StopCaptureCommand.NotifyCanExecuteChanged();
    }

    // ── frame path ──────────────────────────────────────────────────────────────
    private void OnPcmReceived(byte[] pcm, int sampleRate, int channels) // native rt thread
    {
        // Only the level + counter are shown; the bytes themselves aren't rendered.
        int rms = _audio.LastRms();
        long frames = _audio.FrameCount();
        Dispatcher.UIThread.Post(() => { RmsLevel = rms; FramesCounter = frames; });
    }

    private void Refresh(AudioPermission p)
    {
        IsGranted = p == AudioPermission.Granted;
        PermissionStatus = p switch
        {
            AudioPermission.Granted => "Granted",
            AudioPermission.NotDetermined => "Not requested",
            _ => "Not granted",
        };
        OnPropertyChanged(nameof(StatusClass));
        StartCaptureCommand.NotifyCanExecuteChanged();
    }

    private static string DescribeError(int rc) => rc switch
    {
        -2 => "no input / permission (grant Microphone)",
        -3 => "already running",
        -4 => "null callback",
        -6 => "audio converter setup failed",
        -7 => "audio engine failed to start",
        _ => "native error",
    };
}
