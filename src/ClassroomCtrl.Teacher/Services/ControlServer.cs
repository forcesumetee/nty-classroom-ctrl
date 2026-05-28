using ClassroomCtrl.Networking;
using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;

namespace ClassroomCtrl.Teacher.Services;

public class ControlServer : IDisposable
{
    private readonly ILogger<ControlServer> _logger;
    private readonly TcpControlServer _tcp;
    private readonly Guid _teacherId = Guid.NewGuid();

    // Breakout state: endpointId → roomId (null/missing = main classroom)
    private readonly Dictionary<Guid, Guid?> _studentRoomMap = new();
    private readonly Dictionary<Guid, string> _roomNames = new();
    // Phase 8.5: roomId → hostStudentId. Missing key = no host.
    private readonly Dictionary<Guid, Guid> _roomHostMap = new();

    // Phase 9.1: Student Demonstration — at most one source student is broadcasting to peers
    // at a time. While set, every StudentStreamFrame from this sender is also rebroadcast as
    // a DemoFrame to all students.
    private Guid? _currentDemoSourceId;
    private string _currentDemoSourceName = "";

    // Phase 4.6: Live Mic Monitor — students whose audio the teacher is listening to.
    private readonly HashSet<Guid> _micMonitorTargets = new();

    public event EventHandler<HelloMessage>? StudentJoined;
    public event EventHandler<Guid>? StudentLeft;
    public event EventHandler<ChatMessage>? ChatReceived;
    public event EventHandler<HandRaiseMessage>? HandRaiseReceived;
    public event EventHandler<ScreenshotResponseMessage>? ScreenshotReceived;
    /// <summary>Phase 4 Part 2: Frame received from a student that's streaming back to teacher.</summary>
    public event EventHandler<(Guid StudentId, ScreenStreamFrameMessage Frame)>? StudentStreamFrameReceived;

    /// <summary>Phase 4 Part 3b: Student turned mic on (talkback).</summary>
    public event EventHandler<Guid>? StudentAudioStreamStarted;
    /// <summary>Phase 4 Part 3b: Audio frame received from a student.</summary>
    public event EventHandler<(Guid StudentId, AudioStreamFrameMessage Frame)>? StudentAudioFrameReceived;
    /// <summary>Phase 4 Part 3b: Student turned mic off.</summary>
    public event EventHandler<Guid>? StudentAudioStreamStopped;

    /// <summary>Phase 4 Part 5: Per-student reception quality report (every ~2 seconds).</summary>
    public event EventHandler<(Guid StudentId, ScreenStreamQualityReportMessage Report)>? QualityReportReceived;

    /// <summary>Phase 13: Student submitted quiz answers.</summary>
    public event EventHandler<ClassroomCtrl.Exam.Shared.QuizAnswerSubmitPayload>? QuizSubmissionReceived;

    /// <summary>Phase 8.5: Host of a breakout room changed (or cleared). Args = (roomId, newHostId|null).</summary>
    public event EventHandler<(Guid RoomId, Guid? NewHostId)>? HostChanged;

    /// <summary>Phase 9.1: Student demo started/stopped. Null = stopped.</summary>
    public event EventHandler<(Guid? SourceId, string SourceName)>? DemoStateChanged;

    public Guid? CurrentDemoSourceId => _currentDemoSourceId;

    public ControlServer(ILogger<ControlServer> logger, ILoggerFactory factory, IPAddress localIp)
    {
        _logger = logger;
        _tcp = new TcpControlServer(factory.CreateLogger<TcpControlServer>());

        _tcp.MessageReceived += OnMessage;
        _tcp.PeerConnected += (_, id) =>
        {
            _logger.LogInformation("Peer {Id} TCP connected", id);
            // Phase 11-B inc4 — late-joiner hook.  Forwarded to subscribers (the
            // ScreenBroadcaster) so a student that connects mid-share can be
            // sent ScreenStreamStart + a forced IDR without waiting for the
            // next teacher-initiated Start.
            PeerConnected?.Invoke(this, id);
        };
        _tcp.PeerDisconnected += (_, id) =>
        {
            _logger.LogInformation("Peer {Id} TCP disconnected", id);
            StudentLeft?.Invoke(this, id);
        };
    }

    /// <summary>Phase 11-B inc4 — forwarded TCP peer-connect event.  Subscribers
    /// receive the new peer's id; used by <c>ScreenBroadcaster</c> to deliver a
    /// late-joiner <c>ScreenStreamStart</c> + forced IDR during an active share.</summary>
    public event EventHandler<Guid>? PeerConnected;

    /// <summary>Phase 11-B inc4 — send <c>ScreenStreamStart</c> to ONE peer.
    /// Used by the late-joiner path so existing viewers don't get a duplicate
    /// Start event.  The broadcaster pairs this with a <c>ForceKeyframe</c> so
    /// the next outgoing frame is an IDR the new joiner can decode immediately.</summary>
    public Task SendScreenStreamStartToPeerAsync(Guid peerId, CancellationToken ct)
    {
        var env = Envelope.Create(MessageType.ScreenStreamStart, Array.Empty<byte>(), _teacherId);
        _logger.LogInformation("Late-joiner {Id}: sending ScreenStreamStart", peerId);
        return _tcp.SendAsync(peerId, env, ct);
    }

    public Task StartAsync(CancellationToken ct) => _tcp.StartAsync(ct);

    public Task BroadcastChatAsync(string text, CancellationToken ct)
    {
        var msg = new ChatMessage
        {
            SenderId = _teacherId,
            SenderName = "Teacher",
            Text = text,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.BroadcastAsync(Envelope.Create(MessageType.ChatBroadcast, bytes, _teacherId), ct);
    }

    /// <summary>Send a direct message to ONE student (Phase 3 — Direct Messages 1:1).</summary>
    public Task SendDirectMessageAsync(Guid endpointId, string text, CancellationToken ct)
    {
        var msg = new ChatMessage
        {
            SenderId = _teacherId,
            SenderName = "Teacher",
            RecipientId = endpointId,
            Text = text,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        var env = Envelope.CreateTargeted(MessageType.ChatDirect, bytes, _teacherId, endpointId);
        _logger.LogInformation("DM → {Endpoint}: {Text}", endpointId, text);
        return _tcp.BroadcastAsync(env, ct);
    }

    public Task BroadcastLockAsync(bool locked, CancellationToken ct)
    {
        var env = Envelope.Create(
            locked ? MessageType.LockScreen : MessageType.UnlockScreen,
            Array.Empty<byte>(),
            _teacherId);
        _logger.LogInformation("Broadcasting {Type} to all students",
            locked ? "LockScreen" : "UnlockScreen");
        return _tcp.BroadcastAsync(env, ct);
    }

    // ─────── Phase 6: Power commands (broadcast + per-student) ───────

    /// <summary>Broadcast a force power command to all students. Type must be ForceShutdown/ForceRestart/ForceLogoff.</summary>
    public Task BroadcastPowerAsync(MessageType type, CancellationToken ct)
    {
        var env = Envelope.Create(type, Array.Empty<byte>(), _teacherId);
        _logger.LogWarning("Broadcasting {Type} to all students", type);
        return _tcp.BroadcastAsync(env, ct);
    }

    /// <summary>Send a force power command to a single student.</summary>
    public Task PowerOneAsync(Guid endpointId, MessageType type, CancellationToken ct)
    {
        var env = Envelope.CreateTargeted(type, Array.Empty<byte>(), _teacherId, endpointId);
        _logger.LogWarning("Targeted {Type} -> {Endpoint}", type, endpointId);
        return _tcp.BroadcastAsync(env, ct);
    }

    public Task LockOneAsync(Guid endpointId, bool locked, CancellationToken ct)
    {
        var env = Envelope.CreateTargeted(
            locked ? MessageType.LockScreen : MessageType.UnlockScreen,
            Array.Empty<byte>(),
            _teacherId,
            endpointId);

        _logger.LogInformation("Targeted {Type} -> {Endpoint}",
            locked ? "LockScreen" : "UnlockScreen", endpointId);
        return _tcp.BroadcastAsync(env, ct);
    }

    public Task BroadcastPolicyAsync(PolicyApplyMessage policy, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(policy);
        var env = Envelope.Create(MessageType.PolicyApply, bytes, _teacherId);
        _logger.LogInformation("Broadcasting policy: USB={Usb} Optical={Cd} Print={Pr} Procs={Pc} Hosts={Hc}",
            policy.BlockUsbStorage, policy.BlockOpticalDrive, policy.BlockPrinting,
            policy.BlockedProcessNames.Count, policy.BlockedHostnames.Count);
        return _tcp.BroadcastAsync(env, ct);
    }

    public Task BroadcastPolicyRevertAsync(CancellationToken ct)
    {
        var env = Envelope.Create(MessageType.PolicyRevert, Array.Empty<byte>(), _teacherId);
        _logger.LogInformation("Broadcasting policy revert");
        return _tcp.BroadcastAsync(env, ct);
    }

    /// <summary>Per-student policy override (Spec §6.11).</summary>
    public Task ApplyPolicyToOneAsync(Guid endpointId, PolicyApplyMessage policy, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(policy);
        var env = Envelope.CreateTargeted(MessageType.PolicyApply, bytes, _teacherId, endpointId);
        _logger.LogInformation("Targeted policy → {Endpoint}: USB={Usb} CD={Cd} Print={Pr} Apps={Ac} Hosts={Hc}",
            endpointId, policy.BlockUsbStorage, policy.BlockOpticalDrive, policy.BlockPrinting,
            policy.BlockedProcessNames.Count, policy.BlockedHostnames.Count);
        return _tcp.BroadcastAsync(env, ct);
    }

    public Task RevertPolicyForOneAsync(Guid endpointId, CancellationToken ct)
    {
        var env = Envelope.CreateTargeted(MessageType.PolicyRevert, Array.Empty<byte>(), _teacherId, endpointId);
        _logger.LogInformation("Targeted policy revert → {Endpoint}", endpointId);
        return _tcp.BroadcastAsync(env, ct);
    }

    // ─────── Phase 4 Part 1: Teacher → all students screen broadcast ───────

    /// <summary>Send Start/Stop control message for screen sharing.</summary>
    public Task BroadcastScreenStreamControlAsync(bool start, CancellationToken ct)
    {
        var type = start ? MessageType.ScreenStreamStart : MessageType.ScreenStreamStop;
        var env = Envelope.Create(type, Array.Empty<byte>(), _teacherId);
        _logger.LogInformation("Screen stream {Type}", start ? "START" : "STOP");
        return _tcp.BroadcastAsync(env, ct);
    }

    /// <summary>Send a single screen frame to all students.</summary>
    public Task BroadcastScreenFrameAsync(ScreenStreamFrameMessage frame, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(frame);
        var env = Envelope.Create(MessageType.ScreenStreamFrame, bytes, _teacherId);
        return _tcp.BroadcastAsync(env, ct);
    }

    // ─────── Phase 4 Part 2: Student → Teacher view (on-demand) ───────

    /// <summary>Tell a specific student to start streaming their screen back to teacher with the given codec.</summary>
    public Task RequestStudentStreamAsync(Guid studentId, VideoCodec codec, CancellationToken ct)
    {
        var req = new StudentStreamStartRequest { Codec = codec };
        var payload = MessagePack.MessagePackSerializer.Serialize(req);
        var env = Envelope.CreateTargeted(MessageType.StudentStreamStart, payload, _teacherId, studentId);
        _logger.LogInformation("Request student {Id} to start streaming ({Codec})", studentId, codec);
        return _tcp.BroadcastAsync(env, ct);
    }

    /// <summary>Tell a specific student to stop streaming their screen.</summary>
    public Task StopStudentStreamAsync(Guid studentId, CancellationToken ct)
    {
        var env = Envelope.CreateTargeted(MessageType.StudentStreamStop, Array.Empty<byte>(), _teacherId, studentId);
        _logger.LogInformation("Stop student {Id} streaming", studentId);
        return _tcp.BroadcastAsync(env, ct);
    }

    // ─────── Phase 9.1: Student Demonstration ───────

    /// <summary>
    /// Designate <paramref name="sourceId"/> as the demonstrator. Sends DemoStart to every
    /// student so non-source students open a peer-view window, and asks the source student
    /// to start broadcasting their screen (MJPEG to avoid BUG-001).
    /// </summary>
    public async Task BroadcastDemoStartAsync(Guid sourceId, string sourceName, CancellationToken ct)
    {
        // Stop any in-flight previous demo first.
        if (_currentDemoSourceId.HasValue && _currentDemoSourceId.Value != sourceId)
            await BroadcastDemoStopAsync(ct).ConfigureAwait(false);

        _currentDemoSourceId = sourceId;
        _currentDemoSourceName = sourceName ?? "";

        var msg = new DemoStartMessage { SourceStudentId = sourceId, SourceName = _currentDemoSourceName };
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        var env = Envelope.Create(MessageType.DemoStart, bytes, _teacherId);
        _logger.LogInformation("Demo START source={Id} ({Name})", sourceId, sourceName);
        await _tcp.BroadcastAsync(env, ct).ConfigureAwait(false);

        // Begin the source student's stream — frames will arrive as StudentStreamFrame and
        // be relayed automatically by OnMessage while _currentDemoSourceId is set.
        try { await RequestStudentStreamAsync(sourceId, VideoCodec.Mjpeg, ct).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogWarning(ex, "Demo: failed to start source stream"); }

        DemoStateChanged?.Invoke(this, (sourceId, _currentDemoSourceName));
    }

    // ─────── Phase 9.5: Camera Broadcast ───────

    public Task BroadcastCameraStartAsync(CameraStartMessage msg, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.BroadcastAsync(Envelope.Create(MessageType.CameraStart, bytes, _teacherId), ct);
    }

    public Task BroadcastCameraFrameAsync(CameraFrameMessage msg, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.BroadcastAsync(Envelope.Create(MessageType.CameraFrame, bytes, _teacherId), ct);
    }

    public Task BroadcastCameraStopAsync(CancellationToken ct)
        => _tcp.BroadcastAsync(Envelope.Create(MessageType.CameraStop, Array.Empty<byte>(), _teacherId), ct);

    // ─────── Phase 9.6: Net Movie sync ───────

    public Task BroadcastMoviePlayAsync(MoviePlayMessage msg, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.BroadcastAsync(Envelope.Create(MessageType.MoviePlay, bytes, _teacherId), ct);
    }

    public Task BroadcastMoviePauseAsync(MovieSeekMessage msg, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.BroadcastAsync(Envelope.Create(MessageType.MoviePause, bytes, _teacherId), ct);
    }

    public Task BroadcastMovieSeekAsync(MovieSeekMessage msg, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.BroadcastAsync(Envelope.Create(MessageType.MovieSeek, bytes, _teacherId), ct);
    }

    public Task BroadcastMovieStopAsync(CancellationToken ct)
        => _tcp.BroadcastAsync(Envelope.Create(MessageType.MovieStop, Array.Empty<byte>(), _teacherId), ct);

    // ─────── Phase 6.5: Remote Control ───────
    // Phase 12-B (Tier 1) — all 6 Remote* sends now route through the targeted,
    // lossless _inputOutbox channel via TcpControlServer.SendInputAsync.  The
    // legacy BroadcastAsync path put input on the same DropOldest cap-16
    // _outbox as 20 FPS video frames, so under post-inc4 load a key-up or
    // mouse-up could be evicted → stuck modifier / button on the student.
    // TargetEndpointId is retained on the envelope for the student-side
    // IsForMe filter (no longer needed for routing, harmless defense-in-depth).

    public Task SendRemoteControlStartAsync(Guid studentId, CancellationToken ct)
        => _tcp.SendInputAsync(studentId, Envelope.CreateTargeted(MessageType.RemoteControlStart, Array.Empty<byte>(), _teacherId, studentId), ct);

    public Task SendRemoteControlEndAsync(Guid studentId, CancellationToken ct)
        => _tcp.SendInputAsync(studentId, Envelope.CreateTargeted(MessageType.RemoteControlEnd, Array.Empty<byte>(), _teacherId, studentId), ct);

    public Task SendRemoteMouseMoveAsync(Guid studentId, RemoteMouseMoveMessage msg, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.SendInputAsync(studentId, Envelope.CreateTargeted(MessageType.RemoteMouseMove, bytes, _teacherId, studentId), ct);
    }

    public Task SendRemoteMouseClickAsync(Guid studentId, RemoteMouseClickMessage msg, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.SendInputAsync(studentId, Envelope.CreateTargeted(MessageType.RemoteMouseClick, bytes, _teacherId, studentId), ct);
    }

    public Task SendRemoteMouseScrollAsync(Guid studentId, RemoteMouseScrollMessage msg, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.SendInputAsync(studentId, Envelope.CreateTargeted(MessageType.RemoteMouseScroll, bytes, _teacherId, studentId), ct);
    }

    public Task SendRemoteKeyAsync(Guid studentId, RemoteKeyMessage msg, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        return _tcp.SendInputAsync(studentId, Envelope.CreateTargeted(MessageType.RemoteKey, bytes, _teacherId, studentId), ct);
    }

    // ─────── Phase 4.6: Live Mic Monitor (per-student start/stop) ───────

    public Task SendMicMonitorStartAsync(Guid studentId, CancellationToken ct)
        => _tcp.BroadcastAsync(Envelope.CreateTargeted(MessageType.MicMonitorStart, Array.Empty<byte>(), _teacherId, studentId), ct);

    public Task SendMicMonitorStopAsync(Guid studentId, CancellationToken ct)
        => _tcp.BroadcastAsync(Envelope.CreateTargeted(MessageType.MicMonitorStop, Array.Empty<byte>(), _teacherId, studentId), ct);

    // ─────── Phase 9.2: Screen Pen — annotation overlay ───────

    public Task BroadcastDrawingStrokeAsync(DrawingStrokeMessage stroke, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(stroke);
        var env = Envelope.Create(MessageType.DrawingStroke, bytes, _teacherId);
        return _tcp.BroadcastAsync(env, ct);
    }

    public Task BroadcastDrawingClearAsync(CancellationToken ct)
    {
        var env = Envelope.Create(MessageType.DrawingClear, Array.Empty<byte>(), _teacherId);
        return _tcp.BroadcastAsync(env, ct);
    }

    public Task BroadcastDrawingUndoAsync(CancellationToken ct)
    {
        var env = Envelope.Create(MessageType.DrawingUndo, Array.Empty<byte>(), _teacherId);
        return _tcp.BroadcastAsync(env, ct);
    }

    /// <summary>End the current demonstration (no-op if none active).</summary>
    public async Task BroadcastDemoStopAsync(CancellationToken ct)
    {
        var sourceId = _currentDemoSourceId;
        _currentDemoSourceId = null;
        _currentDemoSourceName = "";

        var env = Envelope.Create(MessageType.DemoStop, Array.Empty<byte>(), _teacherId);
        _logger.LogInformation("Demo STOP (was source={Id})", sourceId);
        await _tcp.BroadcastAsync(env, ct).ConfigureAwait(false);

        if (sourceId.HasValue)
        {
            try { await StopStudentStreamAsync(sourceId.Value, ct).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Demo: failed to stop source stream"); }
        }

        DemoStateChanged?.Invoke(this, (null, ""));
    }

    // ─────── Phase 4 Part 3a: Teacher → all students audio broadcast ───────

    /// <summary>Send Start/Stop control message for audio broadcast.</summary>
    public Task BroadcastAudioStreamControlAsync(bool start, CancellationToken ct)
    {
        var type = start ? MessageType.AudioStreamStart : MessageType.AudioStreamStop;
        var env = Envelope.Create(type, Array.Empty<byte>(), _teacherId);
        _logger.LogInformation("Audio stream {Type}", start ? "START" : "STOP");
        return _tcp.BroadcastAsync(env, ct);
    }

    /// <summary>Send a single audio frame to all students.
    /// Phase 11-C — routed through the dedicated audio channel (separate from
    /// the lossy video queue) so a 20 FPS H.264 burst can't evict un-played
    /// audio.  See <see cref="TcpControlServer.BroadcastAudioAsync"/> for the
    /// queue-policy rationale.</summary>
    public Task BroadcastAudioFrameAsync(AudioStreamFrameMessage frame, CancellationToken ct)
    {
        var bytes = MessagePack.MessagePackSerializer.Serialize(frame);
        var env = Envelope.Create(MessageType.AudioStreamFrame, bytes, _teacherId);
        return _tcp.BroadcastAudioAsync(env, ct);
    }

    // ─────── Phase 4 Part 3c: Master mute ───────

    /// <summary>Broadcast a force-mute command to all students. Students with mic on must turn off.</summary>
    public Task BroadcastForceMuteAllAsync(CancellationToken ct)
    {
        var env = Envelope.Create(MessageType.ForceMuteStudentMic, Array.Empty<byte>(), _teacherId);
        _logger.LogInformation("Broadcasting ForceMuteStudentMic to all students");
        return _tcp.BroadcastAsync(env, ct);
    }

    // ─────── Phase 13: Exam System ───────

    /// <summary>Send Exam to all students. Each student's lockdown window opens on receipt.</summary>
    public Task BroadcastQuizStartAsync(ClassroomCtrl.Exam.Shared.Exam exam, Guid sessionId, CancellationToken ct)
    {
        var payload = new ClassroomCtrl.Exam.Shared.QuizStartPayload
        {
            SessionId = sessionId,
            Exam = exam,
            StartedAtUtc = DateTime.UtcNow,
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(payload);
        var env = Envelope.Create(MessageType.QuizStart, bytes, _teacherId);
        _logger.LogInformation("QuizStart broadcast: {Title} ({Q} questions, {Min} min)",
            exam.Title, exam.Questions.Count, exam.TimeLimitMinutes);
        return _tcp.BroadcastAsync(env, ct);
    }

    /// <summary>Tell all students that the exam ended (closes their lockdown window).</summary>
    public Task BroadcastQuizEndAsync(Guid sessionId, bool showResults, CancellationToken ct)
    {
        var payload = new ClassroomCtrl.Exam.Shared.QuizEndPayload
        {
            SessionId = sessionId,
            ShowResultsToStudents = showResults,
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(payload);
        var env = Envelope.Create(MessageType.QuizEnd, bytes, _teacherId);
        _logger.LogInformation("QuizEnd broadcast (session {Id})", sessionId);
        return _tcp.BroadcastAsync(env, ct);
    }

    /// <summary>Unlock one student early (e.g. they finished or had a problem).</summary>
    public Task UnlockOneFromQuizAsync(Guid endpointId, Guid sessionId, CancellationToken ct)
    {
        var payload = new ClassroomCtrl.Exam.Shared.QuizEndPayload
        {
            SessionId = sessionId,
            ShowResultsToStudents = true,
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(payload);
        var env = Envelope.CreateTargeted(MessageType.QuizUnlockEarly, bytes, _teacherId, endpointId);
        _logger.LogInformation("QuizUnlockEarly → {Endpoint}", endpointId);
        return _tcp.BroadcastAsync(env, ct);
    }

    // ─────── Phase 8: Breakout Rooms ───────

    /// <summary>Assign student to a breakout room. roomId=null removes from any room.</summary>
    public Task AssignToRoomAsync(Guid endpointId, Guid? roomId, string roomName, CancellationToken ct)
    {
        _studentRoomMap[endpointId] = roomId;
        if (roomId.HasValue && !_roomNames.ContainsKey(roomId.Value))
            _roomNames[roomId.Value] = roomName;

        // Carry the current host for this room if any (Phase 8.5).
        Guid? hostId = null;
        if (roomId.HasValue && _roomHostMap.TryGetValue(roomId.Value, out var h)) hostId = h;

        var msg = new BreakoutAssignMessage
        {
            RoomId = roomId ?? Guid.Empty,
            RoomName = roomName ?? "",
            HostStudentId = hostId,
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        var env = Envelope.CreateTargeted(MessageType.BreakoutAssign, bytes, _teacherId, endpointId);

        _logger.LogInformation("Breakout assign → {Endpoint}: room={Room} ({Name}) host={Host}",
            endpointId, roomId, roomName, hostId);
        return _tcp.BroadcastAsync(env, ct);
    }

    // ─────── Phase 8.5: Host assignment + actions ───────

    /// <summary>Set or clear the host of a breakout room. Re-broadcasts BreakoutAssign to every member.</summary>
    public async Task SetRoomHostAsync(Guid roomId, Guid? newHostId, CancellationToken ct)
    {
        if (roomId == Guid.Empty) return;
        if (newHostId.HasValue) _roomHostMap[roomId] = newHostId.Value;
        else _roomHostMap.Remove(roomId);

        var name = _roomNames.TryGetValue(roomId, out var n) ? n : "";
        var members = _studentRoomMap.Where(kv => kv.Value == roomId).Select(kv => kv.Key).ToList();
        foreach (var ep in members)
        {
            await AssignToRoomAsync(ep, roomId, name, ct);
        }
        HostChanged?.Invoke(this, (roomId, newHostId));
        _logger.LogInformation("Host of room {Room} → {Host} (members={Count})", roomId, newHostId, members.Count);
    }

    private Guid? FindRoomWhereHostIs(Guid endpointId)
    {
        foreach (var kv in _roomHostMap)
            if (kv.Value == endpointId) return kv.Key;
        return null;
    }

    /// <summary>Broadcast a chat from "Host of {RoomName}" to students NOT in the host's room.</summary>
    public Task BroadcastHostMessageToMainAsync(Guid hostId, string text, CancellationToken ct)
    {
        var roomId = FindRoomWhereHostIs(hostId);
        if (roomId == null) return Task.CompletedTask;
        var roomName = _roomNames.TryGetValue(roomId.Value, out var n) ? n : "Group";

        var chat = new ChatMessage
        {
            SenderId = hostId,
            SenderName = $"Host of {roomName}",
            Text = text,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(chat);

        // Send to all students NOT in the host's room (i.e. main classroom).
        foreach (var (ep, rid) in _studentRoomMap)
        {
            if (rid == roomId.Value) continue;
            var env = Envelope.CreateTargeted(MessageType.ChatBroadcast, bytes, _teacherId, ep);
            _ = _tcp.BroadcastAsync(env, ct);
        }
        // Also raise locally so MainViewModel logs it in teacher chat.
        ChatReceived?.Invoke(this, chat);
        _logger.LogInformation("Host {Host} of room {Room} → main: {Text}", hostId, roomId, text);
        return Task.CompletedTask;
    }

    /// <summary>Targeted force-mute by host on a peer in same room. Reuses existing ForceMuteStudentMic.</summary>
    public Task HostMutePeerAsync(Guid hostId, Guid targetId, CancellationToken ct)
    {
        var hostRoom = FindRoomWhereHostIs(hostId);
        if (hostRoom == null) return Task.CompletedTask;
        // Verify target is in same room.
        if (!_studentRoomMap.TryGetValue(targetId, out var targetRoom) || targetRoom != hostRoom)
        {
            _logger.LogWarning("HostMute denied: target {T} not in host {H}'s room", targetId, hostId);
            return Task.CompletedTask;
        }
        var env = Envelope.CreateTargeted(MessageType.ForceMuteStudentMic, Array.Empty<byte>(), _teacherId, targetId);
        _logger.LogInformation("HostMute {Host} → {Target}", hostId, targetId);
        return _tcp.BroadcastAsync(env, ct);
    }

    // ─────── Phase 5b: Per-student recording PDPA notify ───────

    public Task NotifyStudentRecordingAsync(Guid studentId, bool isRecording, CancellationToken ct)
    {
        var msg = new StudentRecordingNotifyMessage { IsRecording = isRecording };
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        var env = Envelope.CreateTargeted(MessageType.StudentRecordingNotify, bytes, _teacherId, studentId);
        _logger.LogInformation("StudentRecordingNotify → {Endpoint}: {State}", studentId, isRecording);
        return _tcp.BroadcastAsync(env, ct);
    }

    // ─────── Phase 6.6: One-shot screenshot capture ───────

    /// <summary>
    /// Send RequestScreenshot to one student and await the matching ScreenshotResponse.
    /// Returns null on timeout. Caller is responsible for the PDPA notify after success.
    /// </summary>
    public Task<ScreenshotResponseMessage?> RequestScreenshotAsync(Guid studentId, int timeoutMs, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<ScreenshotResponseMessage?>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<ScreenshotResponseMessage>? handler = null;
        handler = (_, msg) =>
        {
            if (msg.StudentId != studentId) return;
            try { ScreenshotReceived -= handler!; } catch { }
            tcs.TrySetResult(msg);
        };
        ScreenshotReceived += handler;

        var env = Envelope.CreateTargeted(MessageType.RequestScreenshot, Array.Empty<byte>(), _teacherId, studentId);
        _ = _tcp.BroadcastAsync(env, ct);

        // Timeout watchdog: cancel the wait + unsubscribe.
        _ = Task.Delay(timeoutMs, ct).ContinueWith(_ =>
        {
            if (!tcs.Task.IsCompleted)
            {
                try { ScreenshotReceived -= handler!; } catch { }
                tcs.TrySetResult(null);
            }
        }, TaskScheduler.Default);

        _logger.LogInformation("RequestScreenshot → {Endpoint} (timeout {Ms}ms)", studentId, timeoutMs);
        return tcs.Task;
    }

    /// <summary>PDPA balloon notify — fires after a successful capture.</summary>
    public Task NotifyStudentScreenshotAsync(Guid studentId, CancellationToken ct)
    {
        var env = Envelope.CreateTargeted(MessageType.StudentScreenshotNotify, Array.Empty<byte>(), _teacherId, studentId);
        _logger.LogInformation("StudentScreenshotNotify → {Endpoint}", studentId);
        return _tcp.BroadcastAsync(env, ct);
    }

    /// <summary>End breakout — send everyone back to main classroom.</summary>
    public async Task DissolveAllRoomsAsync(CancellationToken ct)
    {
        var endpoints = _studentRoomMap.Keys.ToList();
        foreach (var ep in endpoints)
        {
            await AssignToRoomAsync(ep, null, "", ct);
        }
        _studentRoomMap.Clear();
        _roomNames.Clear();
        _logger.LogInformation("All breakout rooms dissolved");
    }

    public Guid? GetStudentRoom(Guid endpointId) =>
        _studentRoomMap.TryGetValue(endpointId, out var rid) ? rid : null;

    public IReadOnlyDictionary<Guid, string> Rooms => _roomNames;

    /// <summary>Phase 9.8: Send a teacher-authored chat message to all members of one breakout room.</summary>
    public async Task BroadcastChatToRoomAsync(Guid roomId, string text, CancellationToken ct)
    {
        var roomName = _roomNames.TryGetValue(roomId, out var n) ? n : "";
        var chat = new ChatMessage
        {
            SenderId = _teacherId,
            SenderName = "Teacher",
            RoomId = roomId,
            Text = text,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(chat);
        foreach (var (ep, rid) in _studentRoomMap)
        {
            if (rid != roomId) continue;
            var env = Envelope.CreateTargeted(MessageType.ChatRoom, bytes, _teacherId, ep);
            await _tcp.BroadcastAsync(env, ct);
        }
        _logger.LogInformation("Multi-room chat: teacher → room {Room} ({Name})", roomId, roomName);
    }

    /// <summary>Route a room chat to all students in the same room as the sender.</summary>
    public async Task RouteRoomChatAsync(ChatMessage chat, Guid senderEndpointId, CancellationToken ct)
    {
        var senderRoom = GetStudentRoom(senderEndpointId);
        if (!senderRoom.HasValue) return;

        chat.RoomId = senderRoom;
        var bytes = MessagePack.MessagePackSerializer.Serialize(chat);

        foreach (var (ep, rid) in _studentRoomMap)
        {
            if (ep == senderEndpointId) continue;
            if (rid != senderRoom) continue;
            var env = Envelope.CreateTargeted(MessageType.ChatRoom, bytes, _teacherId, ep);
            await _tcp.BroadcastAsync(env, ct);
        }
        _logger.LogInformation("Room chat routed: {Sender} → room {Room}", senderEndpointId, senderRoom);
    }

    public async Task BroadcastFileAsync(string filePath, CancellationToken ct)
    {
        const int ChunkSize = 64 * 1024;
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) throw new FileNotFoundException(filePath);

        var transferId = Guid.NewGuid();
        var fileName = fileInfo.Name;
        var size = fileInfo.Length;
        var chunkCount = (int)((size + ChunkSize - 1) / ChunkSize);

        // Phase 10.20 — Net Movie investigation showed the Serilog _logger output
        // landed in %PROGRAMDATA%\NTY\ClassroomCtrl\logs\teacher-*.log and the dev
        // was checking %TEMP%\teacher-debug.log instead, so the entire feature
        // looked silent.  Mirror the milestones to App.LogDebug so future
        // investigators see the activity in either log file.
        App.LogDebug($"[BroadcastFile] start: file='{fileName}' size={size} chunks={chunkCount} transferId={transferId}");

        string sha256Hex;
        using (var fs = File.OpenRead(filePath))
        using (var sha = SHA256.Create())
        {
            var hash = await sha.ComputeHashAsync(fs, ct);
            sha256Hex = Convert.ToHexString(hash);
        }

        var announce = new FileAnnounceMessage
        {
            TransferId = transferId,
            FileName = fileName,
            SizeBytes = size,
            Sha256Hex = sha256Hex,
            ChunkCount = chunkCount,
            UseMulticast = false,
        };
        var announceBytes = MessagePack.MessagePackSerializer.Serialize(announce);
        // Phase 10.21 — file transfer MUST use the reliable broadcast path.
        // Phase 10.10 Fix 8 introduced a DropOldest bounded channel per peer
        // sized for screen-share frames (16 slots); FileAnnounce/Chunk/Complete
        // were being silently evicted under any sustained burst, which
        // truncated received files to ~10 MB regardless of original size
        // (root cause of the Phase 10.21 "Net Movie plays only first third"
        // report).  Reliable path is FullMode.Wait per peer, so the producer
        // here is back-pressured by the slowest student's TCP drain rate
        // instead of corrupting the stream.
        await _tcp.BroadcastReliableAsync(Envelope.Create(MessageType.FileAnnounce, announceBytes, _teacherId), ct);
        _logger.LogInformation("File announce: {Name} ({Size} bytes, {Chunks} chunks)",
            fileName, size, chunkCount);
        App.LogDebug($"[BroadcastFile] FileAnnounce sent: sha256={sha256Hex[..16]}...");

        using (var fs = File.OpenRead(filePath))
        {
            var buffer = new byte[ChunkSize];
            int idx = 0;
            int read;
            while ((read = await fs.ReadAsync(buffer.AsMemory(0, ChunkSize), ct)) > 0)
            {
                var chunkData = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunkData, 0, read);

                var chunk = new FileChunkMessage
                {
                    TransferId = transferId,
                    ChunkIndex = idx,
                    Data = chunkData,
                };
                var chunkBytes = MessagePack.MessagePackSerializer.Serialize(chunk);
                await _tcp.BroadcastReliableAsync(Envelope.Create(MessageType.FileChunk, chunkBytes, _teacherId), ct);
                idx++;
            }
        }

        var complete = new FileCompleteMessage { TransferId = transferId, FileName = fileName };
        var completeBytes = MessagePack.MessagePackSerializer.Serialize(complete);
        await _tcp.BroadcastReliableAsync(Envelope.Create(MessageType.FileComplete, completeBytes, _teacherId), ct);
        _logger.LogInformation("File transfer complete: {Name}", fileName);
        App.LogDebug($"[BroadcastFile] complete: file='{fileName}' chunksSent={chunkCount}");
    }

    private void OnMessage(object? sender, Envelope env)
    {
        switch (env.Type)
        {
            case MessageType.Hello:
                var hello = MessagePack.MessagePackSerializer.Deserialize<HelloMessage>(env.Payload);
                _logger.LogInformation("Student joined: {Name} ({Machine}) endpoint={Id}",
                    hello.DisplayName, hello.MachineName, hello.EndpointId);
                StudentJoined?.Invoke(this, hello);
                break;

            case MessageType.HandRaise:
            case MessageType.HandLower:
                var hr = MessagePack.MessagePackSerializer.Deserialize<HandRaiseMessage>(env.Payload);
                hr.StudentId = env.SenderId;
                hr.IsRaised = env.Type == MessageType.HandRaise;
                _logger.LogInformation("Hand {State} from {Student}",
                    hr.IsRaised ? "RAISED" : "lowered", hr.StudentName);
                HandRaiseReceived?.Invoke(this, hr);
                break;

            case MessageType.ChatBroadcast:
            case MessageType.ChatDirect:
                {
                    var chat = MessagePack.MessagePackSerializer.Deserialize<ChatMessage>(env.Payload);
                    ChatReceived?.Invoke(this, chat);
                }
                break;

            case MessageType.ChatRoom:
                {
                    var roomChat = MessagePack.MessagePackSerializer.Deserialize<ChatMessage>(env.Payload);
                    roomChat.SenderId = env.SenderId;
                    _ = RouteRoomChatAsync(roomChat, env.SenderId, CancellationToken.None);
                    ChatReceived?.Invoke(this, roomChat);
                }
                break;

            case MessageType.ScreenshotResponse:
                try
                {
                    var shot = MessagePack.MessagePackSerializer.Deserialize<ScreenshotResponseMessage>(env.Payload);
                    shot.StudentId = env.SenderId;
                    ScreenshotReceived?.Invoke(this, shot);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decode ScreenshotResponse");
                }
                break;

            case MessageType.StudentStreamFrame:
                try
                {
                    var frame = MessagePack.MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(env.Payload);

                    // Phase 10.15 BUG-001 — log every received student stream frame at the TCP
                    // boundary so we can see whether frames make it across the network at all.
                    // Includes the post-deserialize codec field so we catch a wrong-codec bug
                    // (e.g. default falling back to Mjpeg if the field is lost on the wire).
                    // ScreenStreamFrameMessage.FrameData has `= Array.Empty<byte>()` initializer
                    // so it's never null on the receive side after deserialize.
                    int previewLen = Math.Min(16, frame.FrameData.Length);
                    var preview = previewLen > 0 ? frame.FrameData.AsSpan(0, previewLen).ToArray() : Array.Empty<byte>();
                    var hex = previewLen > 0 ? BitConverter.ToString(preview).Replace("-", " ") : "(empty)";
                    if (frame.FrameSeq <= 5 || frame.IsKeyframe)
                    {
                        App.LogDebug($"[ControlServer.RX] StudentStreamFrame sender={env.SenderId} seq={frame.FrameSeq} codec={frame.Codec} bytes={frame.FrameData.Length} keyframe={frame.IsKeyframe} first16=[{hex}]");
                    }

                    StudentStreamFrameReceived?.Invoke(this, (env.SenderId, frame));

                    // Phase 9.1: if this sender is the demo source, rebroadcast as DemoFrame.
                    if (_currentDemoSourceId.HasValue && env.SenderId == _currentDemoSourceId.Value)
                    {
                        var relay = Envelope.Create(MessageType.DemoFrame, env.Payload, _teacherId);
                        _ = _tcp.BroadcastAsync(relay, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decode StudentStreamFrame");
                    App.LogDebug($"[ControlServer.RX] StudentStreamFrame DESERIALIZE FAILED: {ex.GetType().Name}: {ex.Message}");
                }
                break;

            case MessageType.StudentAudioStreamStart:
                _logger.LogInformation("Student {Id} mic ON", env.SenderId);
                StudentAudioStreamStarted?.Invoke(this, env.SenderId);
                break;

            case MessageType.StudentAudioStreamFrame:
                try
                {
                    // Phase 13-B step 1: removed Phase 9.7 room-voice relay gate
                    // (depended on _roomVoiceMembers which was never populated;
                    //  branch was unreachable).  Tier 3 group voice replaces this
                    //  with VoiceAudioFrame (0x0640) on a dedicated _voiceOutbox.
                    var audio = MessagePack.MessagePackSerializer.Deserialize<AudioStreamFrameMessage>(env.Payload);
                    StudentAudioFrameReceived?.Invoke(this, (env.SenderId, audio));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decode StudentAudioStreamFrame");
                }
                break;

            case MessageType.StudentAudioStreamStop:
                _logger.LogInformation("Student {Id} mic OFF", env.SenderId);
                StudentAudioStreamStopped?.Invoke(this, env.SenderId);
                break;

            // ─────── Phase 8.5: Host action relay ───────
            case MessageType.HostActionMute:
                try
                {
                    var msg = MessagePack.MessagePackSerializer.Deserialize<HostActionMessage>(env.Payload);
                    if (msg.TargetStudentId.HasValue)
                        _ = HostMutePeerAsync(env.SenderId, msg.TargetStudentId.Value, CancellationToken.None);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "HostActionMute decode failed"); }
                break;

            case MessageType.HostActionShare:
                // v1: hard-coded permission denied. Notify host via DM-style chat in the future.
                _logger.LogInformation("HostActionShare from {Sender} — denied (v1)", env.SenderId);
                break;

            case MessageType.HostActionMessageToMain:
                try
                {
                    var msg = MessagePack.MessagePackSerializer.Deserialize<HostActionMessage>(env.Payload);
                    var text = msg.TextOrPayload?.Trim() ?? "";
                    if (text.Length > 0)
                        _ = BroadcastHostMessageToMainAsync(env.SenderId, text, CancellationToken.None);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "HostActionMessageToMain decode failed"); }
                break;

            case MessageType.QuizAnswerSubmit:
                try
                {
                    var sub = MessagePack.MessagePackSerializer.Deserialize<ClassroomCtrl.Exam.Shared.QuizAnswerSubmitPayload>(env.Payload);
                    sub.StudentEndpointId = env.SenderId;
                    _logger.LogInformation("Quiz submission from {Id} ({Name}): {N} answers, auto={Auto}",
                        sub.StudentEndpointId, sub.DisplayName, sub.Answers.Count, sub.IsAutoSubmitted);
                    QuizSubmissionReceived?.Invoke(this, sub);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decode QuizAnswerSubmit");
                }
                break;

            case MessageType.ScreenStreamQualityReport:
                try
                {
                    var report = MessagePack.MessagePackSerializer.Deserialize<ScreenStreamQualityReportMessage>(env.Payload);
                    QualityReportReceived?.Invoke(this, (env.SenderId, report));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decode ScreenStreamQualityReport");
                }
                break;
        }
    }

    public void Dispose() => _tcp.Dispose();
}