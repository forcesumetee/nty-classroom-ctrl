using AForge.Video;
using AForge.Video.DirectShow;
using ClassroomCtrl.Shared.Protocol;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 16-C (Tier 2) — student-side peer cam broadcaster for Conference
/// Mode.  Mirrors the Teacher's <see cref="ClassroomCtrl.Teacher.Services.CameraBroadcastService"/>
/// shape: DirectShow capture → JPEG encode → emit via IPC.  The teacher then
/// fans the frames out to every in-Conference peer via the 16-C relay arms.
///
/// Wire codes: 0x0680 (Start) / 0x0681 (Frame) / 0x0682 (Stop) carried by
/// <see cref="App.Ipc"/>.  Envelope.SenderId is filled by the
/// Service layer with the student's endpoint id.
///
/// Lifecycle:
///   Start(deviceMoniker, sessionId, sourceName)  →  Stop()  →  Dispose()
/// Concurrent Start while active is a no-op.
/// </summary>
public sealed class StudentCameraBroadcaster : IDisposable
{
    private VideoCaptureDevice? _device;
    private int _busy;     // 0/1 lock to drop overlapping frames
    private long _frameSeq;

    public bool IsActive { get; private set; }
    public int JpegQuality { get; set; } = 70;

    /// <summary>Per-session id matching the active ConferenceStartMessage —
    /// stamped into <see cref="ConferenceCameraStartMessage.SessionId"/> so
    /// receivers verify same-session before allocating a decoder.</summary>
    public Guid SessionId { get; private set; }

    /// <summary>Display name carried in the Start envelope so receivers can
    /// label the tile without a separate name lookup.</summary>
    public string SourceName { get; private set; } = "";

    /// <summary>Last error surfaced by the AForge layer.  Set in
    /// <see cref="Start"/> on failure and in the runtime
    /// <see cref="VideoCaptureDevice.VideoSourceError"/> handler when the
    /// cam fails mid-stream.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Fired when capture stops due to a runtime error (NOT a clean
    /// Stop() call).  UI subscribes to refresh button text + show a toast.</summary>
    public event Action? StoppedDueToError;

    /// <summary>Phase 16-C — fired on every successful frame encode with the
    /// JPEG bytes BEFORE the IPC emit.  Subscriber (MainWindow) marshals to
    /// UI dispatcher + pushes to the self-tile preview so the student sees
    /// their own cam without a wire round-trip.</summary>
    public event Action<byte[]>? LocalFrameReady;

    public static List<(string Moniker, string Name)> EnumerateDevices()
    {
        var list = new List<(string, string)>();
        try
        {
            var infos = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            foreach (FilterInfo info in infos)
                list.Add((info.MonikerString, info.Name));
        }
        catch { /* enumeration may fail on bare-metal init; return empty list */ }
        return list;
    }

    public bool Start(string moniker, int width, int height, int fps,
                      Guid sessionId, string sourceName)
    {
        if (IsActive) return false;
        LastError = "";
        SessionId = sessionId;
        SourceName = sourceName ?? "";

        try
        {
            _device = new VideoCaptureDevice(moniker);
            var cap = _device.VideoCapabilities
                .OrderBy(c => Math.Abs(c.FrameSize.Width - width) + Math.Abs(c.FrameSize.Height - height))
                .FirstOrDefault();
            if (cap != null) _device.VideoResolution = cap;

            _device.NewFrame += OnNewFrame;
            _device.VideoSourceError += OnVideoSourceError;
            _device.Start();
            IsActive = true;

            // Emit ConferenceCameraStart up through IPC + TCP.  The Service
            // stamps Envelope.SenderId with the student's endpoint id; the
            // teacher relay stamps payload.SourceEndpointId from there.
            var startMsg = new ConferenceCameraStartMessage
            {
                SessionId = sessionId,
                SourceEndpointId = Guid.Empty,
                SourceName = SourceName,
                Width = width,
                Height = height,
                Fps = fps,
            };
            var bytes = MessagePack.MessagePackSerializer.Serialize(startMsg);
            var env = Envelope.Create(MessageType.ConferenceCameraStart, bytes, Guid.Empty);
            _ = App.Ipc?.SendAsync(env);
            IpcClient.LogToFile($"[StudentCameraBroadcaster] Started ({width}x{height}@{fps}, name='{SourceName}')");
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IpcClient.LogToFile($"[StudentCameraBroadcaster] Start failed: {ex.Message}");
            return false;
        }
    }

    public void Stop()
    {
        if (!IsActive) return;
        try
        {
            if (_device != null)
            {
                _device.SignalToStop();
                _device.WaitForStop();
                _device.NewFrame -= OnNewFrame;
                _device.VideoSourceError -= OnVideoSourceError;
                _device = null;
            }
        }
        catch { }
        IsActive = false;

        try
        {
            var stopMsg = new ConferenceCameraStopMessage { SourceEndpointId = Guid.Empty };
            var bytes = MessagePack.MessagePackSerializer.Serialize(stopMsg);
            var env = Envelope.Create(MessageType.ConferenceCameraStop, bytes, Guid.Empty);
            _ = App.Ipc?.SendAsync(env);
            IpcClient.LogToFile($"[StudentCameraBroadcaster] Stopped (sent {_frameSeq} frames)");
        }
        catch (Exception ex) { IpcClient.LogToFile($"[StudentCameraBroadcaster] Stop send: {ex.Message}"); }

        SessionId = Guid.Empty;
        SourceName = "";
        _frameSeq = 0;
    }

    private void OnVideoSourceError(object? sender, VideoSourceErrorEventArgs e)
    {
        LastError = e.Description ?? "Camera source error";
        IpcClient.LogToFile($"[StudentCameraBroadcaster] Source error: {LastError}");
        try { Stop(); } catch { }
        try { StoppedDueToError?.Invoke(); } catch { }
    }

    private void OnNewFrame(object? sender, NewFrameEventArgs e)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var ms = new MemoryStream();
            EncodeJpeg(e.Frame, ms, JpegQuality);
            var jpeg = ms.ToArray();
            // Phase 16-C — fire local-preview event BEFORE the IPC emit so a
            // slow IPC drain doesn't lag the student's own self-tile.
            try { LocalFrameReady?.Invoke(jpeg); } catch { }
            var msg = new ConferenceCameraFrameMessage
            {
                SourceEndpointId = Guid.Empty,   // server stamps from envelope
                JpegData = jpeg,
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            Interlocked.Increment(ref _frameSeq);
            var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
            var env = Envelope.Create(MessageType.ConferenceCameraFrame, bytes, Guid.Empty);
            _ = App.Ipc?.SendAsync(env);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IpcClient.LogToFile($"[StudentCameraBroadcaster] Frame encode failed: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static void EncodeJpeg(Bitmap bmp, Stream output, int quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var prm = new EncoderParameters(1);
        prm.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        bmp.Save(output, codec, prm);
    }

    public void Dispose() => Stop();
}
