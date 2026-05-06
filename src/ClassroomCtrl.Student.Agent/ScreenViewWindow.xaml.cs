using ClassroomCtrl.Shared.Codec;
using ClassroomCtrl.Shared.Protocol;
using H264Sharp;
using System;
using System.IO;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 4 Part 1 + 4 + 5: Fullscreen viewer for teacher's screen broadcast.
/// Decodes MJPEG (BitmapImage) or H.264 (H264DecoderWrapper + WriteableBitmap),
/// and emits a quality report every ~2 seconds for the teacher's adaptive bitrate
/// controller.
/// </summary>
public partial class ScreenViewWindow : Window
{
    private int _frameCount;
    private long _lastFpsCheckMs;
    private int _lastFpsFrameCount;
    private double _currentFps;

    private H264DecoderWrapper? _h264;
    private WriteableBitmap? _wbmp;

    // ─────── Phase 4 Part 5: quality reporting ───────
    private const int ReportIntervalMs = 2000;
    /// <summary>
    /// Hard-coded expected frame rate. Matches Teacher's ScreenBroadcaster default (4 FPS).
    /// TODO: Derive from broadcast metadata if FPS becomes a teacher-configurable setting.
    /// </summary>
    private const int ExpectedFps = 4;

    private DispatcherTimer? _reportTimer;
    private int _intervalFramesReceived;
    private int _intervalFramesDropped;
    private long _intervalStartUtcMs;

    public ScreenViewWindow()
    {
        InitializeComponent();
        Closed += OnWindowClosed;
        Loaded += OnWindowLoaded;

        // Phase 9 Section A — wire overlay state to MainWindow so the bottom
        // bar's mic icon stays in sync with the actual broadcaster lifecycle.
        // The button label flips between "🎤" (off) and "🔊" (on) to match the
        // mute-mic UX in the rest of the app.
        if (App.MainWindowInstance != null)
        {
            RefreshOverlayMicGlyph(App.MainWindowInstance.IsMicOn);
            App.MainWindowInstance.MicStateChanged += (_, on) =>
                Dispatcher.Invoke(() => RefreshOverlayMicGlyph(on));
        }
    }

    private void RefreshOverlayMicGlyph(bool on)
    {
        OverlayMicButton.Content = on ? "🔊" : "🎤";
        OverlayMicButton.ToolTip = on ? "Mute microphone" : "Unmute microphone";
    }

    // ───── Phase 9 Section A: overlay control handlers ─────
    // Routes through App.MainWindowInstance so audio + IPC paths stay single-
    // sourced; ScreenViewWindow does not own any broadcaster state of its own.

    private void OverlayMic_Click(object sender, RoutedEventArgs e)
        => App.MainWindowInstance?.ToggleMicrophone();

    private void OverlaySend_Click(object sender, RoutedEventArgs e)
        => SubmitOverlayChat();

    private void OverlayChatInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SubmitOverlayChat();
    }

    private void SubmitOverlayChat()
    {
        var text = OverlayChatInput.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text)) return;
        App.MainWindowInstance?.SendChatExternal(text);
        OverlayChatInput.Clear();
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        // Belt-and-suspenders fullscreen: XAML already sets WindowState=Maximized but
        // some WPF show-orderings end up with a default-sized window. Force the size
        // explicitly to the primary screen, then re-apply Maximized.
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        WindowState = WindowState.Maximized;
        Topmost = true;
        Activate();

        _intervalStartUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _reportTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ReportIntervalMs) };
        _reportTimer.Tick += OnReportTick;
        _reportTimer.Start();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _reportTimer?.Stop();
        _reportTimer = null;
        _h264?.Dispose();
        _h264 = null;
    }

    /// <summary>Called from MainWindow on each ScreenStreamFrame message.</summary>
    public void UpdateFrame(byte[] frameData, int width, int height,
        long timestampMs, int frameSeq, VideoCodec codec, bool isKeyframe)
    {
        _intervalFramesReceived++;
        try
        {
            bool rendered = codec == VideoCodec.H264
                ? RenderH264(frameData)
                : RenderMjpeg(frameData);

            if (!rendered)
            {
                _intervalFramesDropped++;
                return;
            }

            WaitingText.Visibility = Visibility.Collapsed;

            _frameCount++;
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (_lastFpsCheckMs == 0) _lastFpsCheckMs = nowMs;
            var elapsed = nowMs - _lastFpsCheckMs;
            if (elapsed >= 1000)
            {
                _currentFps = (_frameCount - _lastFpsFrameCount) * 1000.0 / elapsed;
                _lastFpsCheckMs = nowMs;
                _lastFpsFrameCount = _frameCount;
            }

            StatsText.Text = $"{codec}  ·  {width}×{height}  ·  {_currentFps:F1} FPS  ·  Frame #{frameSeq}";
        }
        catch
        {
            _intervalFramesDropped++;
            // Drop bad frames silently
        }
    }

    private async void OnReportTick(object? sender, EventArgs e)
    {
        var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var intervalMs = Math.Max(1, nowMs - _intervalStartUtcMs);
        var expected = (int)Math.Max(1, intervalMs * ExpectedFps / 1000);

        var received = _intervalFramesReceived;
        var dropped = _intervalFramesDropped;

        // Reset counters BEFORE sending so the next interval starts clean.
        _intervalFramesReceived = 0;
        _intervalFramesDropped = 0;
        var intervalStart = _intervalStartUtcMs;
        _intervalStartUtcMs = nowMs;

        // No real backlog buffer in this pipeline; place-holder for future buffered renderers.
        var bufferDepthMs = 0;

        // Score (0-100): arrivalRate * 100 - dropPenalty*50 - bufferPenalty*50
        var arrivalRate = Math.Min(1.0, (double)received / expected);
        var dropPenalty = received > 0 ? Math.Min(0.5, (double)dropped / received) : 0;
        var bufferPenalty = bufferDepthMs > 500 ? 0.2 : 0;
        var score = (int)Math.Round(arrivalRate * 100 - dropPenalty * 50 - bufferPenalty * 50);
        if (score < 0) score = 0; else if (score > 100) score = 100;

        var report = new ScreenStreamQualityReportMessage
        {
            FramesExpected = expected,
            FramesReceived = received,
            FramesDropped = dropped,
            BufferDepthMs = bufferDepthMs,
            QualityScore = score,
            IntervalStartUtcMs = intervalStart,
            IntervalEndUtcMs = nowMs,
        };

        try
        {
            if (App.Ipc != null)
            {
                var bytes = MessagePack.MessagePackSerializer.Serialize(report);
                var env = Envelope.Create(MessageType.ScreenStreamQualityReport, bytes, Guid.Empty);
                await App.Ipc.SendAsync(env);
            }
        }
        catch
        {
            // Best effort — never let reporting crash playback
        }
    }

    private bool RenderMjpeg(byte[] jpeg)
    {
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(jpeg))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
        }
        ScreenImage.Source = bmp;
        return true;
    }

    private bool RenderH264(byte[] nal)
    {
        _h264 ??= TryCreateDecoder();
        if (_h264 == null) return false;

        if (!_h264.TryDecode(nal, out var rgb, out int w, out int h, out var fmt)) return false;

        var (pixelFormat, bytesPerPixel) = MapFormat(fmt);
        if (_wbmp == null || _wbmp.PixelWidth != w || _wbmp.PixelHeight != h || _wbmp.Format != pixelFormat)
        {
            _wbmp = new WriteableBitmap(w, h, 96, 96, pixelFormat, null);
        }
        _wbmp.WritePixels(new Int32Rect(0, 0, w, h), rgb, w * bytesPerPixel, 0);
        ScreenImage.Source = _wbmp;
        return true;
    }

    private static H264DecoderWrapper? TryCreateDecoder()
    {
        try { return new H264DecoderWrapper(); }
        catch { return null; }
    }

    private static (System.Windows.Media.PixelFormat fmt, int bpp) MapFormat(H264Sharp.ImageFormat f) => f switch
    {
        H264Sharp.ImageFormat.Bgra => (System.Windows.Media.PixelFormats.Bgra32, 4),
        H264Sharp.ImageFormat.Rgba => (System.Windows.Media.PixelFormats.Pbgra32, 4),
        H264Sharp.ImageFormat.Bgr  => (System.Windows.Media.PixelFormats.Bgr24, 3),
        _                          => (System.Windows.Media.PixelFormats.Rgb24, 3),
    };

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        // ESC to close (student can close if teacher's still sharing)
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    // ─────── Phase 9.2: Screen Pen overlay rendering ───────

    /// <summary>Add one teacher-drawn stroke to the overlay InkCanvas.</summary>
    public void AddStroke(DrawingStrokeMessage msg)
    {
        var w = DrawingOverlay.ActualWidth;
        var h = DrawingOverlay.ActualHeight;
        if (w <= 0 || h <= 0 || msg.Points == null || msg.Points.Count < 2) return;

        var pts = new System.Windows.Input.StylusPointCollection();
        foreach (var p in msg.Points)
        {
            pts.Add(new System.Windows.Input.StylusPoint(p.X * w, p.Y * h));
        }

        System.Windows.Media.Color color;
        try { color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(msg.ColorHex); }
        catch { color = Colors.Red; }

        var stroke = new Stroke(pts)
        {
            DrawingAttributes = new DrawingAttributes
            {
                Color = color,
                Width = msg.Thickness,
                Height = msg.Thickness,
                FitToCurve = true,
            }
        };
        DrawingOverlay.Strokes.Add(stroke);
    }

    public void ClearStrokes() => DrawingOverlay.Strokes.Clear();

    public void UndoStroke()
    {
        if (DrawingOverlay.Strokes.Count > 0)
            DrawingOverlay.Strokes.RemoveAt(DrawingOverlay.Strokes.Count - 1);
    }
}