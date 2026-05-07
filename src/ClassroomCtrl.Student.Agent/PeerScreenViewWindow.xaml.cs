using ClassroomCtrl.Shared.Codec;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
using H264Sharp;
using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 9.1: Fullscreen viewer for a peer student's demonstration. Mirrors
/// ScreenViewWindow's decode pipeline but with a different banner color and no
/// outbound quality reporting (only the source-student channel reports back).
/// </summary>
public partial class PeerScreenViewWindow : Window
{
    private int _frameCount;
    private long _lastFpsCheckMs;
    private int _lastFpsFrameCount;
    private double _currentFps;

    private H264DecoderWrapper? _h264;
    private WriteableBitmap? _wbmp;

    public PeerScreenViewWindow(string sourceName)
    {
        InitializeComponent();
        Closed += (_, _) => { _h264?.Dispose(); _h264 = null; };
        Loaded += OnWindowLoaded;
        HeaderText.Text = Loc.Format("Lbl_DemoActive", sourceName);
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        WindowState = WindowState.Maximized;
        Topmost = true;
        Activate();
    }

    public void UpdateFrame(byte[] frameData, int width, int height,
        long timestampMs, int frameSeq, VideoCodec codec, bool isKeyframe)
    {
        try
        {
            bool rendered = codec == VideoCodec.H264 ? RenderH264(frameData) : RenderMjpeg(frameData);
            if (!rendered) return;

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
            // Drop bad frames silently
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

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // Don't allow ESC to close — closes only when teacher sends DemoStop
    }
}
