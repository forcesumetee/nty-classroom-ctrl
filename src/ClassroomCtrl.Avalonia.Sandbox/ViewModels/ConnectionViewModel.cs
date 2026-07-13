using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using ClassroomCtrl.Shared.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MessagePack;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>One row in the wire-traffic debug log.</summary>
public sealed class WireLogRow
{
    public required string Time { get; init; }
    public required string Direction { get; init; }   // "TX" / "RX" / "SYS"
    public required string Glyph { get; init; }        // ▲ / ▼ / •
    public required string Label { get; init; }
    public required string Size { get; init; }
    /// <summary>Decoded, human-readable summary (filled in Phase 26.0-C dispatch).</summary>
    public string Detail { get; init; } = "";
}

/// <summary>
/// Phase 26.0 — VM behind the Connection tab. Owns a single <see cref="WireClient"/>
/// (stable EndpointId across reconnects → one persistent tile on the Teacher) and
/// marshals its background-thread events onto the UI thread via
/// <see cref="Dispatcher.UIThread"/>. The Connect flow reuses the Phase 25.7-C IP
/// validation (now <see cref="IpValidation"/>).
/// </summary>
public partial class ConnectionViewModel : ObservableObject
{
    private const int MaxLogRows = 500;

    public WireClient Client { get; } = new();

    /// <summary>Reflects the state Teacher commands drive onto this student.</summary>
    public StudentSelfTileViewModel SelfTile { get; } = new();

    /// <summary>Phase 27-C — screen → JPEG → StudentStreamFrame streamer.</summary>
    private readonly ScreenStreamer _streamer = new();

    /// <summary>Phase 28-E — camera → JPEG → ConferenceCameraFrame streamer (peer cam).</summary>
    private readonly CameraStreamer _cameraStreamer = new();

    /// <summary>Phase 29-E — mic → PCM → StudentAudioStreamFrame streamer (talkback).</summary>
    private readonly AudioStreamer _audioStreamer = new();

    /// <summary>Phase 29-F — playback of the Teacher's AudioStreamFrame broadcast (path A).
    /// Receive-side; independent native engine, coexists with the send-side streamers.</summary>
    private readonly AudioCaptureService _audioPlayback = new();
    private int _playbackSeq;

    /// <summary>Phase 30-C — enforces the teacher screen-lock (native shield) + owns the
    /// four-layer dead-man switch (process-kill / 45 s disconnect grace / 30 min cap / wake).</summary>
    private readonly LockService _lock = new();

    /// <summary>Active Conference session (from the Teacher's ConferenceStart); Empty
    /// when not in a Conference. Camera peer-cam frames are gated on this.</summary>
    private Guid _conferenceSessionId;

    private CancellationTokenSource? _cts;

    [ObservableProperty] private string teacherIp = "172.20.10.7";  // Phase 24.2 hotspot subnet
    [ObservableProperty] private string portText = "7777";
    [ObservableProperty] private string displayName = $"Mac Sandbox ({Environment.MachineName})";
    [ObservableProperty] private string errorMessage = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(IsDisconnected))]
    [NotifyPropertyChangedFor(nameof(IsConnected))]
    [NotifyPropertyChangedFor(nameof(IsFullyConnected))]
    [NotifyPropertyChangedFor(nameof(IsConnectingState))]
    [NotifyPropertyChangedFor(nameof(StatusClass))]
    private WireStatus status = WireStatus.Disconnected;

    public ObservableCollection<WireLogRow> Log { get; } = new();

    public string StatusText => Status switch
    {
        WireStatus.Connected => "Connected",
        WireStatus.Connecting => "Connecting…",
        WireStatus.Reconnecting => "Reconnecting…",
        _ => "Disconnected",
    };

    /// <summary>Style-class hook for the status pill.</summary>
    public string StatusClass => Status switch
    {
        WireStatus.Connected => "connected",
        WireStatus.Connecting or WireStatus.Reconnecting => "connecting",
        _ => "disconnected",
    };

    public bool IsDisconnected => Status == WireStatus.Disconnected;
    public bool IsConnected => Status != WireStatus.Disconnected;
    public bool IsFullyConnected => Status == WireStatus.Connected;
    public bool IsConnectingState => Status is WireStatus.Connecting or WireStatus.Reconnecting;

    public ConnectionViewModel()
    {
        // WireClient raises on background threads → marshal to the UI thread.
        Client.StatusChanged += s => Post(() =>
        {
            Status = s;
            SelfTile.IsOnline = s == WireStatus.Connected;
            SelfTile.DisplayName = DisplayName;
            // Dead-man layer 2: feed EVERY transition to the lock (grace timer). Do this
            // BEFORE the disconnect reset so the lock (and its shield) survives the grace
            // window — SelfTile.Reset() no longer touches IsLocked (LockService owns it).
            _lock.OnConnectionStatus(s);
            if (s == WireStatus.Disconnected)
            {
                _ = _streamer.StopAsync();
                _ = _cameraStreamer.StopAsync();
                _ = _audioStreamer.StopAsync();
                _ = _audioPlayback.StopPlaybackAsync();
                _conferenceSessionId = Guid.Empty;
                SelfTile.Reset();
            }
            ConnectCommand.NotifyCanExecuteChanged();
            DisconnectCommand.NotifyCanExecuteChanged();
            RaiseHandCommand.NotifyCanExecuteChanged();
        });
        Client.Traffic += (dir, label, size) => Post(() => AddLog(dir, label, size));
        Client.EnvelopeReceived += env => Post(() => Dispatch(env));
        _streamer.FrameSent += seq => Post(() => SelfTile.StreamedFrames = seq);
        _cameraStreamer.FrameSent += seq => Post(() => SelfTile.CameraFrames = seq);
        _audioStreamer.FrameSent += seq => Post(() => SelfTile.MicFrames = seq);
        // LockService is the single owner of SelfTile.IsLocked — it stays true through the
        // disconnect grace window and flips false on explicit/auto unlock.
        _lock.LockStateChanged += up => Post(() => SelfTile.IsLocked = up);
        _lock.Log += msg => Post(() => AddLog(WireDirection.System, $"lock: {msg}", 0));
    }

    /// <summary>Decode the requested codec from a StudentStreamStartRequest; default
    /// MJPEG if the payload is absent/unreadable (older Teacher builds send empty).</summary>
    private static VideoCodec DecodeStreamCodec(byte[] payload)
    {
        try
        {
            if (payload.Length == 0) return VideoCodec.Mjpeg;
            return MessagePackSerializer.Deserialize<StudentStreamStartRequest>(payload).Codec;
        }
        catch { return VideoCodec.Mjpeg; }
    }

    private async Task StartStreamingAsync(VideoCodec codec)
    {
        int rc = await _streamer.StartAsync(Client, codec);
        Post(() =>
        {
            SelfTile.IsStreaming = rc == 0;
            SelfTile.StreamCodec = codec == VideoCodec.H264 ? "H.264" : "MJPEG";
            if (rc == 0) SelfTile.LastDeferred = "";
            else SelfTile.LastDeferred = $"StudentStreamStart · capture failed (code {rc})";
            AddLog(WireDirection.System, rc == 0 ? $"screen streaming started ({codec})" : $"stream start failed ({rc})", 0);
        });
    }

    private async Task StopStreamingAsync()
    {
        await _streamer.StopAsync();
        Post(() =>
        {
            SelfTile.IsStreaming = false;
            AddLog(WireDirection.System, "screen streaming stopped", 0);
        });
    }

    /// <summary>Decode the Conference SessionId from a ConferenceStartMessage; Empty
    /// if the payload is absent/unreadable.</summary>
    private static Guid DecodeConferenceSessionId(byte[] payload)
    {
        try
        {
            if (payload.Length == 0) return Guid.Empty;
            return MessagePackSerializer.Deserialize<ConferenceStartMessage>(payload).SessionId;
        }
        catch { return Guid.Empty; }
    }

    /// <summary>28-E — start the peer-cam stream for the active Conference session.
    /// Chosen default when the Camera Capture tab is already using the camera: the
    /// native camera is a single session, so <see cref="CameraStreamer.StartAsync"/>
    /// returns -3 (already running). We RESPECT the manual preview — log a clear note
    /// and do NOT emit a Start (StartAsync only sends Start after rc==0, so nothing
    /// spurious goes on the wire).</summary>
    private async Task StartCameraStreamingAsync(Guid sessionId)
    {
        int rc = await _cameraStreamer.StartAsync(Client, Client.EndpointId, sessionId, DisplayName);
        Post(() =>
        {
            SelfTile.IsCameraLive = rc == 0;
            if (rc == 0)
                AddLog(WireDirection.System, "camera peer-cam started", 0);
            else if (rc == -3)
                AddLog(WireDirection.System, "camera busy (Camera Capture tab active?) — peer-cam not started", 0);
            else
                AddLog(WireDirection.System, $"camera peer-cam start failed ({rc})", 0);
        });
    }

    private async Task StopCameraStreamingAsync()
    {
        await _cameraStreamer.StopAsync();
        Post(() =>
        {
            SelfTile.IsCameraLive = false;
            AddLog(WireDirection.System, "camera peer-cam stopped", 0);
        });
    }

    /// <summary>29-E — start mic talkback for the Teacher's Mic Monitor. Same
    /// "respect the manual Audio-tab preview" default as camera: the native mic is a
    /// single session, so if the Audio Capture tab is already capturing,
    /// <see cref="AudioStreamer.StartAsync"/> returns -3 — we log and emit no Start.</summary>
    private async Task StartMicAsync()
    {
        int rc = await _audioStreamer.StartAsync(Client, Client.EndpointId);
        Post(() =>
        {
            SelfTile.IsMicLive = rc == 0;
            if (rc == 0)
                AddLog(WireDirection.System, "mic talkback started", 0);
            else if (rc == -3)
                AddLog(WireDirection.System, "mic busy (Audio Capture tab active?) — talkback not started", 0);
            else
                AddLog(WireDirection.System, $"mic talkback start failed ({rc})", 0);
        });
    }

    private async Task StopMicAsync()
    {
        await _audioStreamer.StopAsync();
        Post(() =>
        {
            SelfTile.IsMicLive = false;
            AddLog(WireDirection.System, "mic talkback stopped", 0);
        });
    }

    // ── teacher-audio playback (path A, 29-F) ────────────────────────────────────
    private async Task StartAudioPlaybackAsync()
    {
        _playbackSeq = 0;
        int rc = await _audioPlayback.StartPlaybackAsync(16000, 1);
        Post(() =>
        {
            SelfTile.IsPlayingAudio = rc == 0;
            AddLog(WireDirection.System, rc == 0 ? "teacher audio playback started" : $"playback start failed ({rc})", 0);
        });
    }

    /// <summary>Enqueue an inbound teacher-audio frame for playback (called ~10/s, no
    /// per-frame logging). Lazily starts the engine if AudioStreamStart was dropped
    /// (the broadcast start rides the lossy channel).</summary>
    private void PlayTeacherAudio(byte[] payload)
    {
        try
        {
            var msg = MessagePackSerializer.Deserialize<AudioStreamFrameMessage>(payload);
            if (!_audioPlayback.IsPlaying)
                _ = _audioPlayback.StartPlaybackAsync(msg.SampleRate > 0 ? msg.SampleRate : 16000,
                                                      msg.Channels > 0 ? msg.Channels : 1);
            _audioPlayback.EnqueuePcm(msg.PcmData);
            int n = Interlocked.Increment(ref _playbackSeq);
            Post(() => { SelfTile.IsPlayingAudio = true; SelfTile.PlayedAudioFrames = n; });
        }
        catch { /* transient decode; next frame lands ~100 ms later */ }
    }

    private async Task StopAudioPlaybackAsync()
    {
        await _audioPlayback.StopPlaybackAsync();
        Post(() =>
        {
            SelfTile.IsPlayingAudio = false;
            AddLog(WireDirection.System, "teacher audio playback stopped", 0);
        });
    }

    private static string Short(Guid id) => id == Guid.Empty ? "—" : id.ToString("N")[..8];

    /// <summary>Decode an inbound envelope: enrich the traffic log with a summary
    /// and reflect the command onto <see cref="SelfTile"/>. No enforcement.
    /// Public so HeadlessCapture can seed a representative state deterministically.</summary>
    public void Dispatch(Envelope env)
    {
        // High-frequency inbound audio frames (path A, ~10/s): enqueue for playback and
        // return BEFORE the per-envelope AddLog — logging each would flood the wire log.
        if (env.Type == MessageType.AudioStreamFrame)
        {
            PlayTeacherAudio(env.Payload);
            return;
        }

        string detail;
        switch (env.Type)
        {
            case MessageType.Pong:
                detail = "keepalive ack";
                break;
            // Phase 30-C — ENFORCE (not just reflect). Empty payload (matches Windows) →
            // fixed message. The shield + four-layer dead-man live in LockService.
            case MessageType.LockScreen:
                _lock.Lock(null);
                detail = "🔒 lock screen (enforced)";
                break;
            case MessageType.UnlockScreen:
                _lock.Unlock();
                detail = "🔓 unlock screen";
                break;
            case MessageType.PolicyApply:
                detail = ApplyPolicy(env.Payload);
                break;
            case MessageType.PolicyRevert:
                SelfTile.SetPolicy(Array.Empty<string>());
                detail = "revert policy";
                break;
            case MessageType.ChatBroadcast:
            case MessageType.ChatDirect:
            case MessageType.ChatRoom:
                detail = ApplyChat(env.Payload);
                break;
            case MessageType.HandRaise:
                SelfTile.IsHandRaised = true;
                detail = "hand raised";
                break;
            case MessageType.HandLower:
                SelfTile.IsHandRaised = false;
                detail = "hand lowered";
                break;
            // Phase 27-C — the Teacher's "View Screen" request. Start real
            // ScreenCaptureKit → JPEG → StudentStreamFrame streaming.
            case MessageType.StudentStreamStart:
                var codec = DecodeStreamCodec(env.Payload);
                _ = StartStreamingAsync(codec);
                detail = $"▶ streaming screen to teacher ({codec})";
                break;
            case MessageType.StudentStreamStop:
                _ = StopStreamingAsync();
                detail = "■ stream stopped";
                break;
            // Phase 28-E — the Teacher started Conference Mode. Capture the session id
            // and start streaming this Mac's camera as a peer cam (JPEG, 320×240).
            case MessageType.ConferenceStart:
                _conferenceSessionId = DecodeConferenceSessionId(env.Payload);
                _ = StartCameraStreamingAsync(_conferenceSessionId);
                detail = $"▶ conference started — streaming camera ({Short(_conferenceSessionId)})";
                break;
            case MessageType.ConferenceEnd:
                _ = StopCameraStreamingAsync();
                _conferenceSessionId = Guid.Empty;
                detail = "■ conference ended — camera stopped";
                break;
            // Phase 29-E — the Teacher's Mic Monitor "Listen". Start real AVAudioEngine
            // → PCM → StudentAudioStreamFrame talkback (the teacher hears this Mac's mic).
            case MessageType.MicMonitorStart:
                _ = StartMicAsync();
                detail = "▶ mic talkback to teacher (Mic Monitor)";
                break;
            case MessageType.MicMonitorStop:
                _ = StopMicAsync();
                detail = "■ mic talkback stopped";
                break;
            // Phase 29-F — the Teacher broadcasts audio (own mic / system audio, path A).
            // Play it through the Mac speakers (AVAudioEngine player + jitter buffer).
            case MessageType.AudioStreamStart:
                _ = StartAudioPlaybackAsync();
                detail = "▶ playing teacher audio";
                break;
            case MessageType.AudioStreamStop:
                _ = StopAudioPlaybackAsync();
                detail = "■ teacher audio stopped";
                break;
            // Remaining capture-class commands: logged + noted, deferred until their
            // native macOS APIs land. No frames produced.
            case MessageType.RequestScreenshot:
            case MessageType.ScreenStreamStart:
            case MessageType.CameraStart:
                detail = "deferred — needs native capture (Phase 27+)";
                SelfTile.LastDeferred = $"{env.Type} · deferred (needs native APIs)";
                break;
            default:
                detail = "";
                break;
        }
        AddLog(WireDirection.Rx, env.Type.ToString(), Client.LastRxSize, detail);
    }

    private string ApplyPolicy(byte[] payload)
    {
        try
        {
            var p = MessagePackSerializer.Deserialize<PolicyApplyMessage>(payload);
            var chips = new List<string>();
            if (p.BlockUsbStorage) chips.Add("USB");
            if (p.BlockOpticalDrive) chips.Add("CD/DVD");
            if (p.BlockPrinting) chips.Add("Print");
            if (p.BlockedProcessNames.Count > 0) chips.Add($"{p.BlockedProcessNames.Count} apps");
            if (p.BlockedHostnames.Count > 0) chips.Add($"{p.BlockedHostnames.Count} sites");
            SelfTile.SetPolicy(chips);
            return chips.Count == 0 ? "apply policy (none)" : "apply policy: " + string.Join(", ", chips);
        }
        catch (Exception ex) { return $"policy decode failed: {ex.Message}"; }
    }

    private string ApplyChat(byte[] payload)
    {
        try
        {
            var c = MessagePackSerializer.Deserialize<ClassroomCtrl.Shared.Protocol.ChatMessage>(payload);
            SelfTile.LastChat = $"{c.SenderName}: {c.Text}";
            return $"💬 {c.SenderName}: {c.Text}";
        }
        catch (Exception ex) { return $"chat decode failed: {ex.Message}"; }
    }

    private bool CanConnect() => Status == WireStatus.Disconnected;

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        ErrorMessage = "";
        if (!IpValidation.IsValidIpv4(TeacherIp))
        {
            ErrorMessage = "Enter a valid teacher IPv4 address (e.g. 172.20.10.7)";
            return;
        }
        if (!int.TryParse(PortText.Trim(), out var port) || port is < 1 or > 65535)
        {
            ErrorMessage = "Port must be 1–65535";
            return;
        }

        _cts = new CancellationTokenSource();
        // Fire-and-forget: RunAsync only returns when Disconnect cancels it.
        _ = Client.RunAsync(TeacherIp.Trim(), port, DisplayName.Trim(), _cts.Token);
    }

    private bool CanDisconnect() => Status != WireStatus.Disconnected;

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private void Disconnect() => _cts?.Cancel();

    private bool CanRaiseHand() => Status == WireStatus.Connected;

    /// <summary>Bonus S→T: toggle + send a HandRaise so the Teacher tile lights up.</summary>
    [RelayCommand(CanExecute = nameof(CanRaiseHand))]
    private async Task RaiseHand()
    {
        var raise = !SelfTile.IsHandRaised;
        SelfTile.IsHandRaised = raise;
        var msg = new HandRaiseMessage
        {
            StudentId = Client.EndpointId,
            StudentName = DisplayName,
            IsRaised = raise,
        };
        try
        {
            await Client.SendAsync(raise ? MessageType.HandRaise : MessageType.HandLower,
                                   MessagePackSerializer.Serialize(msg), CancellationToken.None);
        }
        catch (Exception ex) { AddLog(WireDirection.System, $"hand-raise send failed: {ex.Message}", 0); }
    }

    [RelayCommand]
    private void ClearLog() => Log.Clear();

    /// <summary>Append a traffic row (already on the UI thread). Detail/summary for
    /// RX frames is enriched by the dispatcher in Phase 26.0-C.</summary>
    public void AddLog(WireDirection dir, string label, int size, string detail = "")
    {
        var (name, glyph) = dir switch
        {
            WireDirection.Tx => ("TX", "▲"),
            WireDirection.Rx => ("RX", "▼"),
            _ => ("SYS", "•"),
        };
        Log.Add(new WireLogRow
        {
            Time = DateTime.Now.ToString("HH:mm:ss.fff"),
            Direction = name,
            Glyph = glyph,
            Label = label,
            Size = size > 0 ? $"{size} B" : "",
            Detail = detail,
        });
        while (Log.Count > MaxLogRows) Log.RemoveAt(0);
    }

    private static void Post(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }
}
