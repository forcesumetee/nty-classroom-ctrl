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
using System.Threading.Tasks;

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
///   StartAsync(deviceMoniker, sessionId, sourceName) → Stop() → Dispose()
/// Concurrent Start while active is a no-op.
///
/// Phase 16-X (Bug C fix, 2026-05-31) — AForge initialization is now performed
/// on a background <see cref="Task.Run"/> with a 10-second timeout.  Accessing
/// <see cref="VideoCaptureDevice.VideoCapabilities"/> performs a synchronous
/// DirectShow filter-graph build that can block the calling thread for several
/// seconds on some Windows / driver combinations.  Pre-fix, that hang ran
/// directly on the WPF UI thread, causing Student.Agent to enter "Not
/// Responding" until force-close.  Teacher's <c>CameraBroadcastService</c>
/// carries the same latent risk; flagged for a follow-up polish round.
/// </summary>
public sealed class StudentCameraBroadcaster : IDisposable
{
    /// <summary>Phase 16-X — guard against a wedged DirectShow init.  10 s
    /// covers slow USB cameras + first-time driver load; anything longer is
    /// almost certainly a hung filter-graph build and the UI should surface
    /// a localized error instead of hanging the user out to dry.</summary>
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Phase 16-X — clean Stop on a busy capture thread occasionally
    /// blocks past WaitForStop's internal join.  Give it 3 s before we give
    /// up and let the device leak quietly; user-facing UI keeps moving.</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

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
    /// <see cref="StartAsync"/> on failure and in the runtime
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

    /// <summary>Phase 16-X (Bug C fix) — device enumeration also performs a
    /// DirectShow query that can block; static so callers (the toolbar's
    /// click handler in MainWindow) can await it before opening the
    /// selector dialog.  Synchronous overload kept below for the
    /// pre-existing call sites that already run on a background thread.</summary>
    public static Task<List<(string Moniker, string Name)>> EnumerateDevicesAsync(CancellationToken ct = default)
        => Task.Run(EnumerateDevices, ct);

    /// <summary>Phase 16-X (Bug E fix, 2026-05-31) — DirectShow moniker prefix
    /// for physical (PnP) capture devices.  Virtual cameras
    /// (LSVCam / OBS Virtual Cam / Snap Cam / Manycam etc.) register under
    /// <c>@device:sw:</c> and almost always fail AForge bring-up with
    /// 0x8007045A ERROR_DLL_INIT_FAILED because their drivers expect a
    /// host app (LiveSwitch / OBS / Snap) to be running.  Skipping them
    /// at enumeration time means the broadcaster picks the real hardware
    /// camera even when the user has half a dozen ghosts installed.</summary>
    private const string PnpMonikerPrefix = "@device:pnp:";

    public static List<(string Moniker, string Name)> EnumerateDevices()
    {
        // All discovered devices (hardware + virtual) for the diagnostic
        // log line below; the returned list is hardware-only.
        var allEnumerated = new List<(string Moniker, string Name, bool IsHardware)>();
        var hardwareOnly = new List<(string, string)>();
        try
        {
            var infos = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            foreach (FilterInfo info in infos)
            {
                var moniker = info.MonikerString ?? "";
                var isHardware = moniker.StartsWith(PnpMonikerPrefix, StringComparison.Ordinal);
                allEnumerated.Add((moniker, info.Name, isHardware));
                if (isHardware) hardwareOnly.Add((moniker, info.Name));
            }
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[StudentCameraBroadcaster] EnumerateDevices threw: {ex.Message}");
            return hardwareOnly;
        }

        // Observability: log every device we saw with the pnp/sw classification
        // so a future "wrong camera selected" report is one log-line away.
        if (allEnumerated.Count == 0)
        {
            IpcClient.LogToFile("[StudentCameraBroadcaster] Enumerated devices: (none)");
        }
        else
        {
            var sb = new System.Text.StringBuilder("[StudentCameraBroadcaster] Enumerated devices:");
            foreach (var (_, name, isHw) in allEnumerated)
            {
                sb.Append("\n  - ").Append(name).Append(isHw ? " (pnp)" : " (sw, skipped)");
            }
            IpcClient.LogToFile(sb.ToString());
        }

        return hardwareOnly;
    }

    /// <summary>Phase 16-X — async cam init.  Heavy AForge work
    /// (<see cref="VideoCaptureDevice.VideoCapabilities"/> filter-graph
    /// enumeration + device <c>Start()</c>) runs on a background thread so
    /// the caller's UI thread keeps pumping messages.  Returns false on
    /// timeout / driver error; <see cref="LastError"/> carries the cause.
    /// </summary>
    public async Task<bool> StartAsync(string moniker, int width, int height, int fps,
                                       Guid sessionId, string sourceName,
                                       CancellationToken ct = default)
    {
        if (IsActive) return false;
        LastError = "";
        SessionId = sessionId;
        SourceName = sourceName ?? "";

        IpcClient.LogToFile($"[StudentCameraBroadcaster] StartAsync begin (moniker={moniker}, target={width}x{height}@{fps})");

        try
        {
            // Run the slow AForge bring-up on a background thread.  The two
            // synchronous hot spots (filter-graph build via VideoCapabilities
            // + native StartRecording) both finish here before we touch any
            // UI-thread state in the caller's continuation.
            var initTask = Task.Run(() =>
            {
                IpcClient.LogToFile("[StudentCameraBroadcaster] AForge init: constructing VideoCaptureDevice");
                var device = new VideoCaptureDevice(moniker);
                IpcClient.LogToFile("[StudentCameraBroadcaster] AForge init: querying VideoCapabilities");
                var cap = device.VideoCapabilities
                    .OrderBy(c => Math.Abs(c.FrameSize.Width - width) + Math.Abs(c.FrameSize.Height - height))
                    .FirstOrDefault();
                if (cap != null)
                {
                    device.VideoResolution = cap;
                    IpcClient.LogToFile($"[StudentCameraBroadcaster] AForge init: selected resolution {cap.FrameSize.Width}x{cap.FrameSize.Height}");
                }
                else
                {
                    IpcClient.LogToFile("[StudentCameraBroadcaster] AForge init: no matching VideoCapabilities — using device default");
                }
                return device;
            }, ct);

            var timeoutTask = Task.Delay(InitTimeout, ct);
            var completed = await Task.WhenAny(initTask, timeoutTask).ConfigureAwait(false);
            if (completed == timeoutTask)
            {
                LastError = $"Camera init timed out after {(int)InitTimeout.TotalSeconds} seconds";
                IpcClient.LogToFile($"[StudentCameraBroadcaster] {LastError} — likely hung DirectShow filter-graph build");
                // initTask keeps running on the threadpool; we abandon it
                // rather than block.  The leaked VideoCaptureDevice (if it
                // ever completes) is GC-collected.  Caller surfaces the
                // timeout to the user via LastError.
                return false;
            }
            _device = await initTask.ConfigureAwait(false);
            IpcClient.LogToFile("[StudentCameraBroadcaster] AForge init: VideoCaptureDevice ready");

            _device.NewFrame += OnNewFrame;
            _device.VideoSourceError += OnVideoSourceError;

            // _device.Start spawns AForge's internal capture thread.  It's
            // usually fast (≪ 1 s) but kept off the UI thread for the same
            // belt-and-suspenders reason as the init above.
            await Task.Run(() => _device.Start(), ct).ConfigureAwait(false);
            IsActive = true;
            IpcClient.LogToFile("[StudentCameraBroadcaster] AForge init: capture thread started");

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
        catch (OperationCanceledException)
        {
            LastError = "Camera init cancelled";
            IpcClient.LogToFile("[StudentCameraBroadcaster] StartAsync cancelled");
            CleanupAfterFailedStart();
            return false;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IpcClient.LogToFile($"[StudentCameraBroadcaster] StartAsync failed: {ex.GetType().Name}: {ex.Message}");
            CleanupAfterFailedStart();
            return false;
        }
    }

    private void CleanupAfterFailedStart()
    {
        // Best-effort tear-down so a partial Start doesn't leave the device
        // half-attached + event handlers still wired.  Swallow exceptions —
        // we've already captured LastError; nothing useful to do here.
        try
        {
            if (_device != null)
            {
                _device.NewFrame -= OnNewFrame;
                _device.VideoSourceError -= OnVideoSourceError;
                try { _device.SignalToStop(); } catch { }
            }
        }
        catch { }
        _device = null;
        IsActive = false;
    }

    public void Stop()
    {
        if (!IsActive) return;
        IpcClient.LogToFile("[StudentCameraBroadcaster] Stop begin");
        var deviceSnapshot = _device;
        IsActive = false;
        _device = null;

        if (deviceSnapshot != null)
        {
            // Phase 16-X — bound the AForge shutdown wait so a wedged capture
            // thread doesn't block the caller's UI.  Fire-and-forget the
            // teardown on the threadpool; the device leaks quietly if
            // WaitForStop never returns.
            _ = Task.Run(() =>
            {
                try
                {
                    deviceSnapshot.NewFrame -= OnNewFrame;
                    deviceSnapshot.VideoSourceError -= OnVideoSourceError;
                    deviceSnapshot.SignalToStop();
                    if (!Task.Run(() => deviceSnapshot.WaitForStop()).Wait(StopTimeout))
                    {
                        IpcClient.LogToFile($"[StudentCameraBroadcaster] AForge WaitForStop > {(int)StopTimeout.TotalSeconds}s — abandoning device");
                    }
                }
                catch (Exception ex)
                {
                    IpcClient.LogToFile($"[StudentCameraBroadcaster] Stop teardown: {ex.Message}");
                }
            });
        }

        try
        {
            var stopMsg = new ConferenceCameraStopMessage { SourceEndpointId = Guid.Empty };
            var bytes = MessagePack.MessagePackSerializer.Serialize(stopMsg);
            var env = Envelope.Create(MessageType.ConferenceCameraStop, bytes, Guid.Empty);
            _ = App.Ipc?.SendAsync(env);
            IpcClient.LogToFile($"[StudentCameraBroadcaster] Stop sent (frames={_frameSeq})");
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
