using ClassroomCtrl.Shared.Protocol;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Captures the primary screen periodically, encodes as JPEG, and pushes
/// to the Service via IPC. The Service forwards to Teacher over TCP.
/// 
/// Output: 240×135 thumbnail @ 60% quality, ~5KB per frame, every 3 seconds.
/// </summary>
public class ScreenCapturer
{
    private const int ThumbnailWidth = 240;
    private const int ThumbnailHeight = 135;
    private const long JpegQuality = 60L;
    private const int IntervalMs = 3000;

    private CancellationTokenSource? _cts;

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => CaptureLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    private async Task CaptureLoop(CancellationToken ct)
    {
        // Get JPEG codec once
        ImageCodecInfo? jpegCodec = null;
        foreach (var c in ImageCodecInfo.GetImageEncoders())
        {
            if (c.MimeType == "image/jpeg") { jpegCodec = c; break; }
        }
        if (jpegCodec == null)
        {
            IpcClient.LogToFile("[ScreenCapturer] No JPEG codec available");
            return;
        }

        var encoderParams = new EncoderParameters(1);
        encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                byte[] jpegBytes = CaptureToJpeg(jpegCodec, encoderParams);
                if (jpegBytes.Length > 0 && App.Ipc != null)
                {
                    var msg = new ScreenshotResponseMessage
                    {
                        StudentId = System.Guid.Empty, // Service stamps SenderId
                        JpegData = jpegBytes,
                        Width = ThumbnailWidth,
                        Height = ThumbnailHeight,
                        CapturedAtUtcMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
                    var env = Envelope.Create(MessageType.ScreenshotResponse, bytes, System.Guid.Empty);
                    await App.Ipc.SendAsync(env, ct);
                }
            }
            catch (System.Exception ex)
            {
                IpcClient.LogToFile($"[ScreenCapturer] error: {ex.Message}");
            }

            try { await Task.Delay(IntervalMs, ct); }
            catch (System.OperationCanceledException) { break; }
        }
    }

    private static byte[] CaptureToJpeg(ImageCodecInfo jpegCodec, EncoderParameters encoderParams)
    {
        // Use virtual screen to capture all monitors as a single area, then resize.
        var bounds = SystemInformation.VirtualScreen;

        using var fullBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(fullBmp))
        {
            g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var thumb = new Bitmap(ThumbnailWidth, ThumbnailHeight, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(thumb))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(fullBmp, 0, 0, ThumbnailWidth, ThumbnailHeight);
        }

        using var ms = new MemoryStream();
        thumb.Save(ms, jpegCodec, encoderParams);
        return ms.ToArray();
    }
}

// Lightweight WinForms-only helper to avoid adding a heavier dependency.
internal static class SystemInformation
{
    public static System.Drawing.Rectangle VirtualScreen
    {
        get
        {
            int x = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
            int y = GetSystemMetrics(77); // SM_YVIRTUALSCREEN
            int cx = GetSystemMetrics(78); // SM_CXVIRTUALSCREEN
            int cy = GetSystemMetrics(79); // SM_CYVIRTUALSCREEN
            return new System.Drawing.Rectangle(x, y, cx, cy);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}