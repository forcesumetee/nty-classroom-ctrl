using ClassroomCtrl.Shared.Codec;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 4 Part 2: Fullscreen viewer for ONE student's screen.
/// Subscribes to App.Server.StudentStreamFrameReceived, filters by StudentId,
/// renders incoming JPEG frames. ESC or Close → tells the student to stop streaming.
/// </summary>
public partial class StudentScreenWindow : Window
{
    private readonly Guid _studentId;
    private readonly string _studentName;

    private int _frameCount;
    private long _lastFpsCheckMs;
    private int _lastFpsFrameCount;
    private double _currentFps;
    private bool _stopRequested;

    public StudentScreenWindow(Guid studentId, string studentName)
    {
        InitializeComponent();

        _studentId = studentId;
        _studentName = studentName;

        // Title shown in the OS title bar (Mode A) — Python-style "Control Panel: {Name}".
        Title = Loc.Format("Lbl_ControlPanel", studentName);
        WaitingText.Text = Loc.Format("Lbl_ViewingStudent", studentName);

        if (App.Server != null)
        {
            App.Server.StudentStreamFrameReceived += OnStudentFrame;
        }

        // Phase 5b: register so MainViewModel can enable per-tile REC dim/light visuals.
        if (System.Windows.Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm)
        {
            vm.ViewingStudents.Add(studentId);
            vm.NotifyViewingStudentsChanged();
        }

        // Reflect any in-flight per-student recording in the local Record button label/color.
        UpdateRecordButton(App.PerStudentRecording?.IsRecording(_studentId) == true);

        Closed += OnClosed;
    }

    /// <summary>
    /// Defense-in-depth: even if the DynamicResource binding misses (timing edge case
    /// or registry-overridden language), force-write the i18n text into each TextBlock
    /// after the first render so the Control Panel never shows blank action buttons.
    /// </summary>
    protected override void OnContentRendered(System.EventArgs e)
    {
        base.OnContentRendered(e);
        try
        {
            RemoteText.Text = Loc.Get("Btn_RemoteMode", "🖱️ Remote Control");
            LockText.Text = Loc.Get(_isLocked ? "Btn_Unlock" : "Btn_LockThis", "🔒 Lock This PC");
            ScreenshotText.Text = Loc.Get("Btn_Screenshot", "📸 Take Screenshot");
            RecordText.Text = Loc.Get(
                App.PerStudentRecording?.IsRecording(_studentId) == true ? "Btn_StopRecord" : "Btn_RecordVideo",
                "🔴 Record Video");
            FullscreenText.Text = Loc.Get(_isFullscreen ? "Btn_Windowed" : "Btn_Fullscreen", "🖥️ Fullscreen");
        }
        catch { /* never let UI text refresh crash the window */ }
    }

    /// <summary>
    /// Phase 5b/UI: Window opens in Mode A (windowed, resizable) by default. The teacher can
    /// toggle to Mode B (fullscreen, borderless, topmost) via the 🖥️ button in the header.
    /// ESC exits fullscreen if active, otherwise closes the window.
    /// </summary>
    private bool _isFullscreen;
    private bool _isLocked;

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen();
    }

    private void EnterFullscreen()
    {
        if (_isFullscreen) return;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        WindowState = WindowState.Maximized;
        SetButtonText(FullscreenButton, Loc.Get("Btn_Windowed"));
        CloseButton.Visibility = Visibility.Visible;
        _isFullscreen = true;
    }

    private void ExitFullscreen()
    {
        if (!_isFullscreen) return;
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        Topmost = false;
        SetButtonText(FullscreenButton, Loc.Get("Btn_Fullscreen"));
        CloseButton.Visibility = Visibility.Collapsed;
        _isFullscreen = false;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ─────── Phase 6.5 — Remote Control ───────

    private bool _remoteActive;

    private async void Remote_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        if (_remoteActive)
        {
            await App.Server.SendRemoteControlEndAsync(_studentId, CancellationToken.None);
            _remoteActive = false;
            UnhookRemoteCapture();
            SetButtonText(RemoteButton, Loc.Get("Btn_RemoteMode"));
            RemoteButton.Background = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
        }
        else
        {
            await App.Server.SendRemoteControlStartAsync(_studentId, CancellationToken.None);
            _remoteActive = true;
            HookRemoteCapture();
            SetButtonText(RemoteButton, Loc.Get("Btn_EndRemote"));
            RemoteButton.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));
        }
    }

    private void HookRemoteCapture()
    {
        ScreenImage.MouseMove += OnRemoteMouseMove;
        ScreenImage.MouseDown += OnRemoteMouseDown;
        ScreenImage.MouseUp += OnRemoteMouseUp;
        ScreenImage.MouseWheel += OnRemoteMouseWheel;
        KeyDown += OnRemoteKeyDown;
        KeyUp += OnRemoteKeyUp;
    }

    private void UnhookRemoteCapture()
    {
        ScreenImage.MouseMove -= OnRemoteMouseMove;
        ScreenImage.MouseDown -= OnRemoteMouseDown;
        ScreenImage.MouseUp -= OnRemoteMouseUp;
        ScreenImage.MouseWheel -= OnRemoteMouseWheel;
        KeyDown -= OnRemoteKeyDown;
        KeyUp -= OnRemoteKeyUp;
    }

    private async void OnRemoteMouseMove(object sender, MouseEventArgs e)
    {
        if (!_remoteActive || App.Server == null) return;
        var p = e.GetPosition(ScreenImage);
        double w = ScreenImage.ActualWidth, h = ScreenImage.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var msg = new ClassroomCtrl.Shared.Protocol.RemoteMouseMoveMessage
        { NormalizedX = Math.Clamp(p.X / w, 0, 1), NormalizedY = Math.Clamp(p.Y / h, 0, 1) };
        try { await App.Server.SendRemoteMouseMoveAsync(_studentId, msg, CancellationToken.None); } catch { }
    }

    private async void OnRemoteMouseDown(object sender, MouseButtonEventArgs e) => await SendClick(e, true);
    private async void OnRemoteMouseUp(object sender, MouseButtonEventArgs e) => await SendClick(e, false);

    private async Task SendClick(MouseButtonEventArgs e, bool isDown)
    {
        if (!_remoteActive || App.Server == null) return;
        int btn = e.ChangedButton switch
        {
            MouseButton.Left => 1,
            MouseButton.Right => 2,
            MouseButton.Middle => 3,
            _ => 0,
        };
        if (btn == 0) return;
        var msg = new ClassroomCtrl.Shared.Protocol.RemoteMouseClickMessage { Button = btn, IsDown = isDown };
        try { await App.Server.SendRemoteMouseClickAsync(_studentId, msg, CancellationToken.None); } catch { }
    }

    private async void OnRemoteMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_remoteActive || App.Server == null) return;
        var msg = new ClassroomCtrl.Shared.Protocol.RemoteMouseScrollMessage { Delta = e.Delta };
        try { await App.Server.SendRemoteMouseScrollAsync(_studentId, msg, CancellationToken.None); } catch { }
    }

    private async void OnRemoteKeyDown(object sender, KeyEventArgs e) => await SendKey(e, true);
    private async void OnRemoteKeyUp(object sender, KeyEventArgs e) => await SendKey(e, false);

    private async Task SendKey(KeyEventArgs e, bool isDown)
    {
        if (!_remoteActive || App.Server == null) return;
        // Don't forward F11/ESC — they control the local window.
        if (e.Key == Key.F11 || e.Key == Key.Escape) return;
        var vk = KeyInterop.VirtualKeyFromKey(e.Key);
        var msg = new ClassroomCtrl.Shared.Protocol.RemoteKeyMessage
        {
            VirtualKeyCode = vk,
            IsDown = isDown,
            ShiftDown = (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
            CtrlDown = (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            AltDown = (Keyboard.Modifiers & ModifierKeys.Alt) != 0,
        };
        try { await App.Server.SendRemoteKeyAsync(_studentId, msg, CancellationToken.None); } catch { }
        e.Handled = true;
    }

    // ─────── Lock toggle (per-student) ───────

    private async void Lock_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        try
        {
            _isLocked = !_isLocked;
            await App.Server.LockOneAsync(_studentId, _isLocked, CancellationToken.None);
            SetButtonText(LockButton, Loc.Get(_isLocked ? "Btn_Unlock" : "Btn_LockThis"));
            LockButton.Background = new SolidColorBrush(_isLocked
                ? Color.FromRgb(0xDC, 0x26, 0x26)   // red while locked
                : Color.FromRgb(0xF5, 0x9E, 0x0B)); // amber when ready-to-lock
        }
        catch (Exception ex)
        {
            MessageBox.Show($"{Loc.Get("Btn_LockThis")}: {ex.Message}");
        }
    }

    // ─────── Phase 6.6 — Screenshot ───────

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        ScreenshotButton.IsEnabled = false;
        try
        {
            var resp = await App.Server.RequestScreenshotAsync(_studentId, timeoutMs: 10_000, CancellationToken.None);
            if (resp == null || resp.JpegData.Length == 0)
            {
                MessageBox.Show(Loc.Get("Err_ScreenshotFailed"), Loc.Get("Btn_Screenshot"));
                return;
            }

            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NTY", "ClassroomCtrl", "Screenshots");
            Directory.CreateDirectory(folder);

            var safeName = SanitizeFile(_studentName);
            var filename = $"{safeName}_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.jpg";
            var path = Path.Combine(folder, filename);
            File.WriteAllBytes(path, resp.JpegData);

            // PDPA notify the student (best-effort).
            try { await App.Server.NotifyStudentScreenshotAsync(_studentId, CancellationToken.None); } catch { }

            // Open with default viewer.
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
            }
            catch { }

            // Surface a chat line in the main window so the teacher sees the saved path.
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm)
                vm.AppendSystemChat(Loc.Format("Msg_ScreenshotSaved", path));
        }
        finally
        {
            ScreenshotButton.IsEnabled = true;
        }
    }

    // ─────── Phase 5b — Record toggle ───────

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (App.PerStudentRecording == null) return;

        if (App.PerStudentRecording.IsRecording(_studentId))
        {
            // Stop
            var path = await App.PerStudentRecording.StopAsync(_studentId);
            try
            {
                if (App.Server != null)
                    await App.Server.NotifyStudentRecordingAsync(_studentId, false, CancellationToken.None);
            }
            catch { }
            UpdateRecordButton(false);
            if (System.Windows.Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm)
            {
                vm.AppendSystemChat(Loc.Format("Toast_RecordingStopped", _studentName, path ?? ""));
                var s = vm.Students.FirstOrDefault(x => x.EndpointId == _studentId);
                if (s != null) s.IsBeingRecorded = false;
            }
        }
        else
        {
            // Start
            if (App.PerStudentRecording.Start(_studentId, _studentName, 1280, 720, 4))
            {
                try
                {
                    if (App.Server != null)
                        await App.Server.NotifyStudentRecordingAsync(_studentId, true, CancellationToken.None);
                }
                catch { }
                UpdateRecordButton(true);
                if (System.Windows.Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm)
                {
                    vm.AppendSystemChat(Loc.Format("Toast_RecordingStarted", _studentName));
                    var s = vm.Students.FirstOrDefault(x => x.EndpointId == _studentId);
                    if (s != null) s.IsBeingRecorded = true;
                }
            }
        }
    }

    private void UpdateRecordButton(bool isRecording)
    {
        SetButtonText(RecordButton, Loc.Get(isRecording ? "Btn_StopRecord" : "Btn_RecordVideo"));
        RecordButton.Background = new SolidColorBrush(isRecording
            ? Color.FromRgb(0xDC, 0x26, 0x26)   // red while recording
            : Color.FromRgb(0x10, 0xB9, 0x81)); // green idle
    }

    private static void SetButtonText(System.Windows.Controls.Button btn, string text)
    {
        if (btn.Content is System.Windows.Controls.TextBlock tb)
            tb.Text = text;
        else
            btn.Content = text;
    }

    private static string SanitizeFile(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Where(c => !bad.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "Student" : clean;
    }

    private H264DecoderWrapper? _h264;
    private WriteableBitmap? _wbmp;

    private void OnStudentFrame(object? sender, (Guid StudentId, ScreenStreamFrameMessage Frame) e)
    {
        // Phase 10.15 BUG-001 — log id-match outcome so we can tell apart
        // (a) frame arrived for the wrong window from (b) frame matched but
        // rendering still failed.  Logged only for first 5 frames + every
        // keyframe to keep log noise low.
        bool idMatch = e.StudentId == _studentId;
        if (e.Frame.FrameSeq <= 5 || e.Frame.IsKeyframe || !idMatch)
        {
            // ScreenStreamFrameMessage.FrameData has `= Array.Empty<byte>()` initializer
            // so it's never null on the receive side; .Length is safe.
            App.LogDebug($"[StudentScreenWindow.OnStudentFrame] sender={e.StudentId} my={_studentId} match={idMatch} seq={e.Frame.FrameSeq} codec={e.Frame.Codec} bytes={e.Frame.FrameData.Length} keyframe={e.Frame.IsKeyframe}");
        }

        if (!idMatch) return;

        Dispatcher.Invoke(() =>
        {
            try
            {
                bool rendered = e.Frame.Codec == VideoCodec.H264
                    ? RenderH264(e.Frame.FrameData)
                    : RenderMjpeg(e.Frame.FrameData);

                // Phase 10.15 BUG-001 — log render outcome at the Dispatcher boundary
                // so we can correlate received-frame vs displayed-frame.
                if (e.Frame.FrameSeq <= 5 || e.Frame.IsKeyframe || !rendered)
                {
                    App.LogDebug($"[StudentScreenWindow.Render] seq={e.Frame.FrameSeq} codec={e.Frame.Codec} rendered={rendered}");
                }

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

                StatsText.Text = $"{e.Frame.Codec}  ·  {e.Frame.Width}×{e.Frame.Height}  ·  {_currentFps:F1} FPS  ·  Frame #{e.Frame.FrameSeq}";
            }
            catch
            {
                // Drop bad frames silently
            }
        });
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

        // Phase 5b: tee BGRA to per-student recorder if active.
        if (App.PerStudentRecording != null && App.PerStudentRecording.IsRecording(_studentId))
        {
            try
            {
                var fmt = System.Windows.Media.PixelFormats.Bgra32;
                var conv = new FormatConvertedBitmap(bmp, fmt, null, 0);
                int w = conv.PixelWidth, h = conv.PixelHeight;
                int stride = w * 4;
                var bgra = new byte[stride * h];
                conv.CopyPixels(bgra, stride, 0);
                App.PerStudentRecording.OnFrame(_studentId, bgra, w, h);
            }
            catch { /* tee never fails the render path */ }
        }
        return true;
    }

    private int _h264FrameCount;

    private bool RenderH264(byte[] nal)
    {
        // Phase 10.15 BUG-001 — file-logged drop diagnostics.  Was Debug.WriteLine,
        // which never surfaced in customer logs (no debugger attached).  Now uses
        // App.LogDebug so the drop trail is persisted at %TEMP%\teacher-debug.log.
        // Also logs first 16 bytes hex so we can compare encoder output vs decoder
        // input — any divergence here points at the transport/framing layer.
        bool decoderWasCreated = _h264 != null;
        _h264 ??= TryCreateDecoder();
        if (_h264 == null)
        {
            App.LogDebug($"[StudentScreenWindow.RenderH264] decoder create FAILED — abandoning frame ({nal.Length} bytes)");
            return false;
        }
        if (!decoderWasCreated)
        {
            App.LogDebug($"[StudentScreenWindow.RenderH264] decoder LAZY-CREATED on first frame ({nal.Length} bytes)");
        }

        if (!_h264.TryDecode(nal, out var rgb, out int w, out int h, out var fmt))
        {
            if (_h264FrameCount < 3)
            {
                int previewLen = Math.Min(16, nal.Length);
                var hex = previewLen > 0
                    ? BitConverter.ToString(nal, 0, previewLen).Replace("-", " ")
                    : "(empty)";
                App.LogDebug($"[StudentScreenWindow.RenderH264] DECODE DROPPED bytes={nal.Length} first16=[{hex}]");
            }
            return false;
        }

        var (pixelFormat, bytesPerPixel) = MapFormat(fmt);
        if (_wbmp == null || _wbmp.PixelWidth != w || _wbmp.PixelHeight != h || _wbmp.Format != pixelFormat)
        {
            _wbmp = new WriteableBitmap(w, h, 96, 96, pixelFormat, null);
        }
        _wbmp.WritePixels(new Int32Rect(0, 0, w, h), rgb, w * bytesPerPixel, 0);
        ScreenImage.Source = _wbmp;
        _h264FrameCount++;

        // Phase 5b: tee BGRA to per-student recorder. Bgra32 is the only format we
        // pipe to ffmpeg; other H.264 output formats (BGR24/RGB24) get skipped.
        if (App.PerStudentRecording != null && App.PerStudentRecording.IsRecording(_studentId)
            && fmt == H264Sharp.ImageFormat.Bgra && rgb != null)
        {
            try { App.PerStudentRecording.OnFrame(_studentId, rgb, w, h); }
            catch { }
        }
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
        if (e.Key == Key.Escape)
        {
            // ESC: exit fullscreen if active, otherwise close.
            if (_isFullscreen) ExitFullscreen();
            else Close();
        }
        else if (e.Key == Key.F11)
        {
            ToggleFullscreen_Click(this, new RoutedEventArgs());
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (App.Server != null)
        {
            App.Server.StudentStreamFrameReceived -= OnStudentFrame;
        }

        // Phase 5b: deregister + auto-stop recording (no more frames coming).
        if (System.Windows.Application.Current?.MainWindow?.DataContext is ViewModels.MainViewModel vm)
        {
            vm.ViewingStudents.Remove(_studentId);
            vm.NotifyViewingStudentsChanged();
        }
        if (App.PerStudentRecording != null && App.PerStudentRecording.IsRecording(_studentId))
        {
            _ = App.PerStudentRecording.StopAsync(_studentId);
        }

        _h264?.Dispose();
        _h264 = null;

        if (!_stopRequested)
        {
            _stopRequested = true;
            try
            {
                _ = App.Server?.StopStudentStreamAsync(_studentId, CancellationToken.None);
            }
            catch
            {
                // Best effort — don't throw on close
            }
        }
    }

    public Guid StudentId => _studentId;
    public string StudentName => _studentName;
}
