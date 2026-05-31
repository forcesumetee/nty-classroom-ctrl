using ClassroomCtrl.Shared.Capture;
using ClassroomCtrl.Shared.Codec;
using ClassroomCtrl.Shared.Protocol;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 21 (v1.1) — student-side Conference screen-share broadcaster.
///
/// Mirrors the Teacher's <see cref="ClassroomCtrl.Teacher.Services.ScreenBroadcaster"/>
/// for the in-frame Conference share path but slimmed for v1.1 scope:
///   • MJPEG only.  H.264 is a v1.2 carry-over per Phase 21 primer; the
///     receive side already gates non-MJPEG at the decoder so a wrong codec
///     would just blank instead of crashing.
///   • 10 FPS, 1280×720 capture target.  Bandwidth at MJPEG q60 ≈ 200–400
///     kbps per student which is well under the 30-student fan-out budget
///     the teacher already absorbs on her Classroom share (~15 Mbps).
///   • Frames ride the IPC envelope upstream → Service forwarder →
///     teacher's ControlServer, which fan-outs to every in-Conference
///     peer (including the teacher's own MainViewModel via the
///     ConferenceShareFrameReceived event added in Phase 21 step 1).
///   • SourceEndpointId in the payload is left empty — the envelope's
///     SenderId stamped by the TCP layer is authoritative, matching the
///     existing <see cref="StudentCameraBroadcaster"/> pattern.
///
/// Self-loopback: the relay echoes back to the sender, but
/// MainWindow.OnIpcMessage already drops ConferenceShareFrame envelopes
/// where <c>env.SenderId == _myEndpointId</c> (see Phase 16-B+ step 10
/// hooks).  So this broadcaster doesn't need to know its own endpoint id.
///
/// Lifecycle: <see cref="Start"/> → repeated <c>CaptureLoopAsync</c> tick
/// → <see cref="Stop"/>.  Concurrent Start while active is a no-op.
/// Concurrent Stop is idempotent.
/// </summary>
public sealed class StudentConferenceShareBroadcaster : IDisposable
{
    private const int TargetWidth = 1280;
    private const int TargetHeight = 720;
    private const int FramesPerSecond = 10;
    private const long JpegQuality = 60L;

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private IScreenCapturer? _capturer;
    private IVideoEncoder? _encoder;
    private int _frameSeq;
    private string _sourceName = "";

    public bool IsActive => _captureTask is { IsCompleted: false };

    /// <summary>Begin the capture+emit loop.  No-op if already active.  Emits
    /// a <see cref="MessageType.ConferenceShareStart"/> envelope on entry so
    /// receivers flip their gallery layout immediately rather than waiting
    /// for the first frame.  Display name is carried in the Start envelope
    /// so the share banner ("X is sharing") doesn't need a separate name
    /// lookup.</summary>
    public void Start(string sourceName)
    {
        if (IsActive) return;
        _sourceName = sourceName ?? "";
        _frameSeq = 0;

        try
        {
            // Reuse the shared GDI/DXGI capture path the Teacher uses for
            // her Classroom share.  VirtualScreen so multi-monitor students
            // share their full desktop, not just primary.  DXGI flag mirrors
            // the existing Student monitoring capturer.
            _capturer = ScreenCapturerFactory.Create(
                ScreenCaptureSource.VirtualScreen,
                useDxgi: App.UseDxgiCapture,
                out string captureDesc);
            _encoder = VideoEncoderFactory.Create(
                VideoCodec.Mjpeg, TargetWidth, TargetHeight,
                FramesPerSecond, bitrateBps: 500_000,
                initialMJpegQuality: JpegQuality,
                useHardwareH264: false,
                out string encoderDesc);
            IpcClient.LogToFile(
                $"[StudentConferenceShareBroadcaster] Start (capture={captureDesc}, encoder={encoderDesc}, " +
                $"target={TargetWidth}x{TargetHeight}@{FramesPerSecond}, name='{_sourceName}')");
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[StudentConferenceShareBroadcaster] Start init failed: {ex.GetType().Name}: {ex.Message}");
            CleanupAfterFailedStart();
            return;
        }

        // Emit ConferenceShareStart so receivers swap to share-view before
        // the first frame lands.  Fire-and-forget; the IPC pipe has its own
        // backpressure handling.
        try
        {
            var startMsg = new ConferenceShareStartMessage
            {
                SourceEndpointId = Guid.Empty,   // server stamps from envelope
                SourceName = _sourceName,
            };
            var bytes = MessagePack.MessagePackSerializer.Serialize(startMsg);
            var env = Envelope.Create(MessageType.ConferenceShareStart, bytes, Guid.Empty);
            _ = App.Ipc?.SendAsync(env);
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[StudentConferenceShareBroadcaster] Start envelope send failed: {ex.Message}");
        }

        _cts = new CancellationTokenSource();
        _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
    }

    /// <summary>Stop the capture loop + emit
    /// <see cref="MessageType.ConferenceShareStop"/>.  Idempotent: a second
    /// Stop call after the loop has already wound down does nothing
    /// (matches the pattern in <see cref="StudentCameraBroadcaster.Stop"/>).
    /// Bounded wait on the capture task so a wedged tick doesn't block the
    /// UI thread; same 2 s budget the Teacher broadcaster uses.</summary>
    public void Stop()
    {
        var captureTask = _captureTask;
        if (captureTask == null) return;

        try { _cts?.Cancel(); } catch { }
        try { captureTask.Wait(2000); } catch { }
        _captureTask = null;

        try { _encoder?.Dispose(); } catch { }
        try { _capturer?.Dispose(); } catch { }
        _encoder = null;
        _capturer = null;

        // Send Stop envelope so receivers swap their gallery back to
        // tile-mode immediately.  Sent AFTER the capture loop is wound down
        // so no in-flight frame trails the Stop on the wire (Service's
        // single-writer SemaphoreSlim serializes the IPC queue).
        try
        {
            var stopMsg = new ConferenceShareStopMessage { SourceEndpointId = Guid.Empty };
            var bytes = MessagePack.MessagePackSerializer.Serialize(stopMsg);
            var env = Envelope.Create(MessageType.ConferenceShareStop, bytes, Guid.Empty);
            _ = App.Ipc?.SendAsync(env);
            IpcClient.LogToFile($"[StudentConferenceShareBroadcaster] Stop sent (frames={_frameSeq})");
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[StudentConferenceShareBroadcaster] Stop envelope send failed: {ex.Message}");
        }

        _cts?.Dispose();
        _cts = null;
        _sourceName = "";
    }

    private void CleanupAfterFailedStart()
    {
        try { _encoder?.Dispose(); } catch { }
        try { _capturer?.Dispose(); } catch { }
        _encoder = null;
        _capturer = null;
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var intervalMs = 1000 / Math.Max(1, FramesPerSecond);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var bmp = _capturer?.Capture(TargetWidth, TargetHeight);
                if (bmp != null)
                {
                    var encoded = _encoder?.Encode(bmp);
                    if (encoded.HasValue)
                    {
                        _frameSeq++;
                        var msg = new ConferenceShareFrameMessage
                        {
                            SourceEndpointId = Guid.Empty,   // server stamps
                            FrameData = encoded.Value.Data,
                            Width = TargetWidth,
                            Height = TargetHeight,
                            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            FrameSeq = _frameSeq,
                            Codec = VideoCodec.Mjpeg,
                            IsKeyframe = true,
                        };
                        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
                        var env = Envelope.Create(MessageType.ConferenceShareFrame, bytes, Guid.Empty);
                        if (App.Ipc != null) await App.Ipc.SendAsync(env, ct);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                IpcClient.LogToFile($"[StudentConferenceShareBroadcaster] Capture/encode failed: {ex.Message}");
            }
            try { await Task.Delay(intervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose() => Stop();
}
