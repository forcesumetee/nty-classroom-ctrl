using ClassroomCtrl.Shared.Codec;
using ClassroomCtrl.Shared.Protocol;
using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 13-C (Tier 2) — modeless viewer for an in-group peer's presented
/// screen.  Decode pipeline mirrors PeerScreenViewWindow (MJPEG / H.264); UX
/// differs: not fullscreen-locked, ESC closes, standard window chrome — Tier 2
/// peer view is collaborative, not teacher-mandated.
///
/// Lifecycle owner: MainWindow.OnEnvelopeFromService.  Open on
/// StudentGroupScreenStreamStart; close on Stop, host change, or group
/// reassignment.  The host's own Agent never opens this window for its own
/// broadcast (sender-side self-filter in MainWindow).
/// </summary>
public partial class GroupPeerView : Window
{
    public Guid PresenterId { get; }
    public Guid GroupId { get; }

    private int _frameCount;
    private long _lastFpsCheckMs;
    private int _lastFpsFrameCount;
    private double _currentFps;

    private ClassroomCtrl.Shared.Codec.H264DecoderWrapper? _h264;
    private WriteableBitmap? _wbmp;

    public GroupPeerView(Guid presenterId, string presenterName, Guid groupId, string groupName)
    {
        InitializeComponent();
        PresenterId = presenterId;
        GroupId = groupId;
        HeaderText.Text = string.IsNullOrEmpty(groupName)
            ? $"📺  Presenting: {presenterName}"
            : $"📺  Presenting: {presenterName}  ·  {groupName}";
        Title = HeaderText.Text;
        Closed += (_, _) => { _h264?.Dispose(); _h264 = null; };
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
            // Drop bad frames silently — mirrors PeerScreenViewWindow.
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

    private static ClassroomCtrl.Shared.Codec.H264DecoderWrapper? TryCreateDecoder()
    {
        try { return new ClassroomCtrl.Shared.Codec.H264DecoderWrapper(); }
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
        if (e.Key == Key.Escape) Close();
    }
}
