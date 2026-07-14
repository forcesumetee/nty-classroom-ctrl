using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ClassroomCtrl.Student.Mac;

/// <summary>
/// macOS Student daemon — the core <see cref="BackgroundService"/> that replaces the
/// Windows <c>ClassroomWorker</c> (<c>ClassroomCtrl.Student.Service</c>).
///
/// Responsibilities:
///   1. Accept IPC connections from the Agent (tray UI) via Unix Domain Socket
///      at <see cref="SocketPath"/>.
///   2. Maintain a TCP connection to the Teacher and route wire-protocol envelopes.
///   3. Dispatch Teacher commands (Lock/Unlock, Power, Policy) to the appropriate
///      macOS-native handler (<see cref="MacPowerCommands"/>, <see cref="LockService"/>).
///   4. Forward UI-bound envelopes (chat, screen share, etc.) to the connected Agent.
///
/// IPC framing is identical to the Windows Named-Pipe IPC and the TCP control
/// protocol: <c>[4-byte big-endian Int32 length] + MessagePack(Envelope)</c>.
/// This keeps the Agent implementation simple — it uses the same read/write loop
/// regardless of transport.
///
/// Design notes:
///   • The daemon is designed to run as a <b>launchd agent</b> (user-level) or
///     <b>launchd daemon</b> (root-level) — both work because macOS UDS doesn't
///     have the cross-session integrity-level issues that Windows Named Pipes have.
///   • The <c>_endpointId</c> is loaded/generated once and persisted to a JSON file
///     so the Teacher recognizes this student across restarts (same contract as Windows).
///   • Thread model: one Task for socket accept, one Task per connected Agent client,
///     one Task for the TCP Teacher connection read loop.
/// </summary>
public sealed class MacClassroomWorker : BackgroundService
{
    /// <summary>
    /// Unix Domain Socket path for Service ↔ Agent IPC.
    /// Placed under the user's temporary directory to avoid permission issues.
    /// The Agent connects here to send/receive wire-protocol envelopes.
    /// </summary>
    public static string SocketPath => IpcSocket.StudentAgentPath;

    /// <summary>Max allowed IPC frame size (16 MB) — same cap as the Windows IPC.</summary>
    private const int MaxFrameSize = 16 * 1024 * 1024;

    private readonly ILogger<MacClassroomWorker> _logger;
    private readonly LockService _lockService;
    private readonly MacPolicyEnforcer _policyEnforcer;
    private readonly Guid _endpointId;

    /// <summary>
    /// Daemon-owned file reassembler. The background service reassembles + SAVES teacher-sent files
    /// itself (to ~/Downloads/NTY ClassroomCtrl) so a closed tray Agent can never break a transfer —
    /// the Agent only receives a lightweight <see cref="FileReceivedNotify"/> for a toast.
    /// </summary>
    private readonly FileReceiver _fileReceiver = new();

    // IPC state
    private Socket? _listenSocket;
    private Socket? _agentSocket;
    private readonly SemaphoreSlim _agentWriteLock = new(1, 1);

    // Teacher TCP state (Phase B)
    private TeacherTcpClient? _teacherClient;

    /// <summary>
    /// Single-consumer inbox for teacher envelopes. The TCP read loop enqueues here and returns
    /// immediately; one consumer task awaits <c>DispatchTeacherCommandAsync</c> in order. This keeps
    /// dispatch strictly sequential (FileAnnounce→Chunk→Complete stay ordered) without ever blocking
    /// the read loop or the IPC accept loop.
    /// </summary>
    private readonly Channel<Envelope> _teacherInbox =
        Channel.CreateUnbounded<Envelope>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>
    /// Screen-broadcast frames bound for the Agent. BOUNDED + DropOldest: a slow/absent Agent can never
    /// bloat daemon memory or stall the dispatch loop — the freshest frames win, stale ones are evicted
    /// (lowest latency). Start/Stop take the reliable ForwardToAgentAsync path; only lossy VideoFrames
    /// ride this channel. Cap is small — these are ENCODED H.264 frames (tens of KB), not decoded bitmaps.
    /// </summary>
    private const int FrameQueueCapacity = 8;
    private readonly Channel<Envelope> _agentFrameChannel =
        Channel.CreateBounded<Envelope>(new BoundedChannelOptions(FrameQueueCapacity)
        { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    // Last teacher-link snapshot, so a freshly-connected Agent can be told the current state at once.
    private readonly object _teacherStatusLock = new();
    private bool _teacherConnected;
    private string _teacherName = "";

    /// <summary>Student display name announced to the Teacher (Hello) and stamped on outgoing chat.</summary>
    private readonly string _displayName = TeacherEndpoint.DisplayName();

    public MacClassroomWorker(
        ILogger<MacClassroomWorker> logger,
        LockService lockService,
        MacPolicyEnforcer policyEnforcer)
    {
        _logger = logger;
        _lockService = lockService;
        _policyEnforcer = policyEnforcer;
        _endpointId = LoadOrCreateEndpointId();
    }

    // ════════════════════════════════════════════════════════════════════
    //  BackgroundService lifecycle
    // ════════════════════════════════════════════════════════════════════

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "MacClassroomWorker starting (endpoint {Id}, PID {Pid}, user {User})",
            _endpointId, Environment.ProcessId,
            $"{Environment.UserDomainName}/{Environment.UserName}");

        LogNetworkInterfaces();
        _logger.LogInformation("[File] Received files will be saved to: {Dir}", _fileReceiver.SaveDirectory);

        // Start the Unix Domain Socket listener for Agent IPC
        StartIpcListener();

        // Accept loop — runs until cancellation
        _ = Task.Run(() => AcceptAgentLoop(ct), ct);

        // Consumer that dispatches teacher envelopes in order (fed by the TCP read loop).
        _ = Task.Run(() => TeacherDispatchLoop(ct), ct);

        // Drains the lossy screen-frame queue → Agent, decoupled from dispatch (never blocks it).
        _ = Task.Run(() => AgentFrameWriterLoop(ct), ct);

        // Phase B — connect to the Teacher over TCP (background; never blocks the loops above).
        StartTeacherConnection(ct);

        // Block until cancellation
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }

        _logger.LogInformation("MacClassroomWorker stopping");
    }

    public override void Dispose()
    {
        _teacherInbox.Writer.TryComplete();
        _agentFrameChannel.Writer.TryComplete();
        _policyEnforcer.Dispose();
        _lockService.Dispose();
        CleanupIpcSocket();
        base.Dispose();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Teacher TCP connection (Phase B)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolve the Teacher endpoint and, if configured, start the reconnecting TCP loop on a background
    /// task. Never blocks the caller (or the IPC accept loop). If no teacher is configured, the loop is
    /// simply not started — the daemon still serves the Agent and can be reconfigured + restarted.
    /// </summary>
    private void StartTeacherConnection(CancellationToken ct)
    {
        if (!TeacherEndpoint.TryResolve(out var ip, out var port))
        {
            _logger.LogWarning(
                "[Teacher] No teacher configured — set NTY_TEACHER_IP or write {File}; TCP loop idle",
                "~/Library/Application Support/NTY/ClassroomCtrl/teacher.txt");
            return;
        }

        var client = new TeacherTcpClient(_logger, _endpointId, _displayName);
        _teacherClient = client;

        client.Connected += () => OnTeacherLinkChanged(connected: true, ip, ct);
        client.Disconnected += () => OnTeacherLinkChanged(connected: false, ip, ct);
        client.EnvelopeReceived += env => _teacherInbox.Writer.TryWrite(env);

        _logger.LogInformation("[Teacher] Starting TCP loop → {Ip}:{Port} as '{Name}'", ip, port, _displayName);
        _ = Task.Run(() => client.RunAsync(ip, port, ct), ct);
    }

    /// <summary>
    /// Handle a teacher-link transition: drive the dead-man switch (drop → 45 s auto-unlock; reconnect →
    /// cancel), record the snapshot, and push it to the Agent for the tray.
    /// </summary>
    private void OnTeacherLinkChanged(bool connected, string ip, CancellationToken ct)
    {
        if (connected) _lockService.OnTeacherReconnected();
        else _lockService.OnTeacherDisconnected();

        lock (_teacherStatusLock)
        {
            _teacherConnected = connected;
            _teacherName = connected ? ip : "";
        }

        _logger.LogInformation("[Teacher] Link {State}{Ip}", connected ? "UP → " : "DOWN", connected ? ip : "");
        _ = PushTeacherStatusAsync(connected, connected ? ip : "", ct);
    }

    /// <summary>Ordered, non-blocking dispatch of queued teacher envelopes (fed by the TCP read loop).</summary>
    private async Task TeacherDispatchLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var env in _teacherInbox.Reader.ReadAllAsync(ct))
            {
                try { await DispatchTeacherCommandAsync(env, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[Teacher] Dispatch failed for {Type}", env.Type); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>Drains the lossy screen-frame queue → Agent. Decoupled from dispatch so a slow Agent
    /// write only affects frame delivery (already lossy), never chat/lock/policy handling.</summary>
    private async Task AgentFrameWriterLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var env in _agentFrameChannel.Reader.ReadAllAsync(ct))
            {
                try { await ForwardToAgentAsync(env, ct); }
                catch (Exception ex) { _logger.LogTrace(ex, "[Screen] frame forward failed"); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>Serialize + forward the teacher-link snapshot to the Agent (best-effort — no-op if none connected).</summary>
    private Task PushTeacherStatusAsync(bool connected, string name, CancellationToken ct)
    {
        var msg = new TeacherStatusMessage { Connected = connected, TeacherName = name };
        var env = Envelope.Create(MessageType.TeacherStatusNotify, MessagePackSerializer.Serialize(msg), _endpointId);
        return ForwardToAgentAsync(env, ct);
    }

    /// <summary>
    /// A student chat reply arrived from the Agent (<see cref="MessageType.ChatSendRequest"/>). The daemon owns
    /// identity, so it builds the real <see cref="ChatMessage"/> (its own endpoint id + display name) and sends it
    /// to the Teacher as a standard <see cref="MessageType.ChatBroadcast"/> — exactly the shape the shipped Student
    /// sends, so the Teacher's ControlServer raises ChatReceived for it.
    /// </summary>
    private async Task HandleAgentChatAsync(Envelope env, CancellationToken ct)
    {
        var client = _teacherClient;
        if (client is null || !client.IsConnected)
        {
            _logger.LogWarning("[Chat] Reply dropped — not connected to Teacher");
            return;
        }

        string text;
        try { text = (MessagePackSerializer.Deserialize<ChatSendRequestMessage>(env.Payload).Text ?? "").Trim(); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Chat] Bad ChatSendRequest payload"); return; }
        if (text.Length == 0) return;

        var chat = new ChatMessage
        {
            SenderId = _endpointId,
            SenderName = _displayName,
            RecipientId = null,   // to the class/teacher (matches the shipped student send)
            Text = text,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        try
        {
            await client.SendAsync(MessageType.ChatBroadcast, MessagePackSerializer.Serialize(chat), ct);
            _logger.LogInformation("[Chat] Student → Teacher: {Text}", text);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Chat] Failed to send reply to Teacher"); }
    }

    /// <summary>
    /// A quiz answer arrived from the Agent (<see cref="MessageType.QuizSubmitRequest"/>). The daemon stamps
    /// its identity into a <see cref="QuizAnswerMessage"/> and sends it to the Teacher on the reserved
    /// <see cref="MessageType.QuizAnswerSubmit"/> codepoint.
    /// </summary>
    private async Task HandleAgentQuizAsync(Envelope env, CancellationToken ct)
    {
        var client = _teacherClient;
        if (client is null || !client.IsConnected)
        {
            _logger.LogWarning("[Quiz] Answer dropped — not connected to Teacher");
            return;
        }

        QuizSubmitRequestMessage req;
        try { req = MessagePackSerializer.Deserialize<QuizSubmitRequestMessage>(env.Payload); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Quiz] Bad QuizSubmitRequest payload"); return; }

        var answer = new QuizAnswerMessage
        {
            QuizId = req.QuizId,
            StudentEndpointId = _endpointId,
            StudentName = _displayName,
            SelectedIndex = req.SelectedIndex,
            SelectedText = req.SelectedText,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        try
        {
            await client.SendAsync(MessageType.QuizAnswerSubmit, MessagePackSerializer.Serialize(answer), ct);
            _logger.LogInformation("[Quiz] Student → Teacher: answer #{Index} '{Text}'", req.SelectedIndex, req.SelectedText);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Quiz] Failed to send answer to Teacher"); }
    }

    // ════════════════════════════════════════════════════════════════════
    //  IPC — Unix Domain Socket listener
    // ════════════════════════════════════════════════════════════════════

    private void StartIpcListener()
    {
        // Ensure the directory exists
        var dir = Path.GetDirectoryName(SocketPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Remove stale socket file from a previous unclean exit
        if (File.Exists(SocketPath))
        {
            _logger.LogInformation("[IPC] Removing stale socket file: {Path}", SocketPath);
            File.Delete(SocketPath);
        }

        _listenSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listenSocket.Bind(new UnixDomainSocketEndPoint(SocketPath));

        // Bind() creates the socket file with the process umask's default perms.
        // Widen to user + group read/write so the Agent (UI) can connect — whether it
        // runs under the same user (user-level launchd agent) or as a group peer of a
        // root-level launchd daemon.  Best-effort: a failure here doesn't stop listening.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            try
            {
                File.SetUnixFileMode(
                    SocketPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IPC] Failed to set socket permissions on {Path}", SocketPath);
            }
        }

        _listenSocket.Listen(backlog: 2);

        _logger.LogInformation("[IPC] Listening on UDS: {Path}", SocketPath);
    }

    private async Task AcceptAgentLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await _listenSocket!.AcceptAsync(ct);
                _logger.LogInformation("[IPC] Agent connected");

                // Only one Agent at a time — disconnect the previous one
                var old = Interlocked.Exchange(ref _agentSocket, clientSocket);
                if (old is not null)
                {
                    _logger.LogInformation("[IPC] Replacing previous Agent connection");
                    try { old.Shutdown(SocketShutdown.Both); } catch { }
                    old.Dispose();
                }

                _ = Task.Run(() => HandleAgentClient(clientSocket, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IPC] Accept error — retrying in 1 s");
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// Read loop for a connected Agent.  Same length-prefixed framing as
    /// the Windows Named-Pipe IPC and the TCP wire protocol.
    /// </summary>
    private async Task HandleAgentClient(Socket socket, CancellationToken ct)
    {
        using var stream = new NetworkStream(socket, ownsSocket: true);
        var lengthBuf = new byte[4];

        // Tell the freshly-connected Agent the current teacher-link state so the tray converges at once
        // (rather than waiting for the next connect/disconnect transition).
        bool tc; string tn;
        lock (_teacherStatusLock) { tc = _teacherConnected; tn = _teacherName; }
        _ = PushTeacherStatusAsync(tc, tn, ct);

        try
        {
            while (!ct.IsCancellationRequested && socket.Connected)
            {
                // Read 4-byte big-endian length prefix
                if (!await ReadExactAsync(stream, lengthBuf, ct))
                {
                    _logger.LogInformation("[IPC] Agent disconnected (clean EOF)");
                    break;
                }

                int frameLen = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
                if (frameLen <= 0 || frameLen > MaxFrameSize)
                {
                    _logger.LogWarning("[IPC] Invalid frame length {Len} — dropping connection", frameLen);
                    break;
                }

                // Read frame payload
                var payload = new byte[frameLen];
                if (!await ReadExactAsync(stream, payload, ct))
                {
                    _logger.LogInformation("[IPC] Agent disconnected (mid-frame EOF)");
                    break;
                }

                // Deserialize and dispatch Agent-originated messages.
                try
                {
                    var env = Envelope.Deserialize(payload);
                    _logger.LogDebug("[IPC] Agent→Service: type={Type} size={Size}", env.Type, frameLen);

                    if (env.Type == MessageType.ChatSendRequest)
                        await HandleAgentChatAsync(env, ct);
                    else if (env.Type == MessageType.QuizSubmitRequest)
                        await HandleAgentQuizAsync(env, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[IPC] Failed to handle Agent frame ({Len} bytes)", frameLen);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IPC] Agent client error");
        }
        finally
        {
            // Clear _agentSocket if this was the current one
            Interlocked.CompareExchange(ref _agentSocket, null, socket);
            _logger.LogInformation("[IPC] Agent session ended");
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Command Router — dispatches Teacher commands to macOS handlers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Central command dispatcher — called when an envelope arrives from the Teacher
    /// (via TCP) or from the Agent (via IPC).  Mirrors the Windows ClassroomWorker's
    /// <c>DispatchAsync</c> switch, ported for macOS-native handlers.
    /// </summary>
    private async Task DispatchTeacherCommandAsync(Envelope env, CancellationToken ct)
    {
        switch (env.Type)
        {
            // ─── Keepalive (connection-level, no action needed) ───
            case MessageType.Ping:
            case MessageType.Pong:
                return;

            // ─── Screen Lock (TOR 11.2.2, 11.2.15, 11.2.16) ───
            case MessageType.LockScreen:
                if (!IsForMe(env)) return;
                _logger.LogInformation("[Dispatch] LockScreen from teacher");
                // Decode optional message from payload
                string lockMsg = "Locked by teacher";
                if (env.Payload.Length > 0)
                {
                    try
                    {
                        var msg = MessagePackSerializer.Deserialize<LockScreenCommandMessage>(env.Payload);
                        if (!string.IsNullOrWhiteSpace(msg.Message))
                            lockMsg = msg.Message;
                    }
                    catch { /* use default message */ }
                }
                _lockService.Lock(lockMsg);
                // Also forward to Agent so it knows lock state
                await ForwardToAgentAsync(env, ct);
                break;

            case MessageType.UnlockScreen:
                if (!IsForMe(env)) return;
                _logger.LogInformation("[Dispatch] UnlockScreen from teacher");
                _lockService.Unlock();
                await ForwardToAgentAsync(env, ct);
                break;

            // ─── Power Commands (TOR 11.2.1, 11.2.17) ───
            case MessageType.ForceShutdown:
                if (!IsForMe(env)) return;
                _logger.LogWarning("[Dispatch] ForceShutdown from teacher (target={Target})", env.TargetEndpointId);
                MacPowerCommands.Shutdown(_logger);
                break;

            case MessageType.ForceRestart:
                if (!IsForMe(env)) return;
                _logger.LogWarning("[Dispatch] ForceRestart from teacher (target={Target})", env.TargetEndpointId);
                MacPowerCommands.Reboot(_logger);
                break;

            case MessageType.ForceLogoff:
                if (!IsForMe(env)) return;
                _logger.LogWarning("[Dispatch] ForceLogoff from teacher (target={Target})", env.TargetEndpointId);
                MacPowerCommands.Logoff(_logger);
                break;

            // ─── Chat (forward to Agent UI) ───
            case MessageType.ChatBroadcast:
                _logger.LogInformation("[Dispatch] Chat broadcast received");
                await ForwardToAgentAsync(env, ct);
                break;

            case MessageType.ChatDirect:
                if (!IsForMe(env)) return;
                _logger.LogInformation("[Dispatch] Direct chat received");
                await ForwardToAgentAsync(env, ct);
                break;

            // ─── Quiz / Survey (Teacher → Student) ───
            // Re-wrap the wire QuizStart payload as an IPC QuizBroadcast so the Agent's protocol stays
            // decoupled from the exam wire codepoints. (Payload = QuizQuestionMessage, forwarded as-is.)
            case MessageType.QuizStart:
                if (!IsForMe(env)) return;
                _logger.LogInformation("[Dispatch] Quiz received from teacher");
                await ForwardToAgentAsync(
                    Envelope.Create(MessageType.QuizBroadcast, env.Payload, _endpointId), ct);
                break;

            // ─── Screen Share (Teacher → Students broadcast) ───
            // Start/Stop are reliable (open/close the viewer); frames are lossy (drop-oldest).
            case MessageType.ScreenStreamStart:
                _logger.LogInformation("[Screen] Broadcast START from teacher");
                await ForwardToAgentAsync(env, ct);
                break;

            case MessageType.ScreenStreamStop:
                _logger.LogInformation("[Screen] Broadcast STOP from teacher");
                await ForwardToAgentAsync(env, ct);
                break;

            case MessageType.ScreenStreamFrame:
                // Non-blocking + bounded: DropOldest evicts a stale frame if the Agent can't keep up,
                // so dispatch never stalls and memory never grows. No await → chat/lock stay responsive.
                _agentFrameChannel.Writer.TryWrite(env);
                break;

            // ─── Student Stream (Teacher requests this student's screen) ───
            case MessageType.StudentStreamStart:
                if (!IsForMe(env)) return;
                _logger.LogInformation("[Dispatch] StudentStreamStart — teacher requesting screen");
                await ForwardToAgentAsync(env, ct);
                break;

            case MessageType.StudentStreamStop:
                if (!IsForMe(env)) return;
                _logger.LogInformation("[Dispatch] StudentStreamStop");
                await ForwardToAgentAsync(env, ct);
                break;

            // ─── Audio broadcast ───
            case MessageType.AudioStreamStart:
            case MessageType.AudioStreamFrame:
            case MessageType.AudioStreamStop:
                await ForwardToAgentAsync(env, ct);
                break;

            // ─── Screenshot request ───
            case MessageType.RequestScreenshot:
                if (!IsForMe(env)) return;
                _logger.LogInformation("[Dispatch] Screenshot requested by teacher");
                await ForwardToAgentAsync(env, ct);
                break;

            // ─── Policy (TOR 11.2.14 — Phase C, app blocking / process kill on macOS) ───
            // Broadcast (TargetEndpointId == Empty) ⇒ class-wide layer; targeted ⇒ per-student layer.
            // Same two-layer semantics as the shipped Windows PolicyEnforcer so mixed classrooms match.
            case MessageType.PolicyApply:
            {
                bool broadcast = env.TargetEndpointId == Guid.Empty && !env.TargetGroupId.HasValue;
                if (!broadcast && env.TargetEndpointId != _endpointId) return;  // targeted at someone else

                var policy = DecodePolicy(env.Payload);
                if (policy is null) return;

                if (broadcast)
                {
                    _logger.LogInformation("[Dispatch] PolicyApply (class-wide) — {Apps} blocked app(s)",
                        policy.BlockedProcessNames.Count);
                    _policyEnforcer.ApplyClass(policy);
                }
                else
                {
                    _logger.LogInformation("[Dispatch] PolicyApply (per-student) — {Apps} blocked app(s)",
                        policy.BlockedProcessNames.Count);
                    _policyEnforcer.ApplyPerStudent(policy);
                }
                await ForwardToAgentAsync(env, ct);   // let the UI reflect the active policy
                break;
            }

            case MessageType.PolicyRevert:
            {
                bool broadcast = env.TargetEndpointId == Guid.Empty && !env.TargetGroupId.HasValue;
                if (!broadcast && env.TargetEndpointId != _endpointId) return;

                if (broadcast)
                {
                    _logger.LogInformation("[Dispatch] PolicyRevert (class-wide)");
                    _policyEnforcer.RevertClass();
                }
                else
                {
                    _logger.LogInformation("[Dispatch] PolicyRevert (per-student)");
                    _policyEnforcer.RevertPerStudent();
                }
                await ForwardToAgentAsync(env, ct);
                break;
            }

            // ─── File transfer — reassembled + SAVED by the daemon itself (headless) ───
            // The Agent (tray UI) is NOT required for the file to land on disk. The daemon owns
            // the whole announce→chunk→complete pipeline and writes to ~/Downloads/NTY ClassroomCtrl;
            // it forwards only a lightweight FileReceivedNotify so the UI can toast IF it's running.
            // (Raw chunks are deliberately NOT re-sent over IPC — no point piping the payload twice.)
            case MessageType.FileAnnounce:
            {
                if (!IsForMe(env)) return;
                try
                {
                    var fa = MessagePackSerializer.Deserialize<FileAnnounceMessage>(env.Payload);
                    _fileReceiver.OnAnnounce(fa);
                    _logger.LogInformation("[File] Incoming '{Name}' ({Size} bytes, {Chunks} chunks)",
                        fa.FileName, fa.SizeBytes, fa.ChunkCount);
                    await SendFileNotifyAsync(new FileReceivedNotifyMessage
                    {
                        TransferId = fa.TransferId,
                        FileName = fa.FileName,
                        SizeBytes = fa.SizeBytes,
                        Status = (byte)FileNotifyStatus.Receiving,
                    }, ct);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[File] Bad FileAnnounce payload — ignored"); }
                break;
            }

            case MessageType.FileChunk:
            {
                if (!IsForMe(env)) return;
                try
                {
                    _fileReceiver.OnChunk(MessagePackSerializer.Deserialize<FileChunkMessage>(env.Payload));
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[File] Bad FileChunk payload — ignored"); }
                break;
            }

            case MessageType.FileComplete:
            {
                if (!IsForMe(env)) return;

                FileCompleteMessage comp;
                try
                {
                    comp = MessagePackSerializer.Deserialize<FileCompleteMessage>(env.Payload);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[File] Bad FileComplete payload — ignored");
                    break;
                }

                // Reassemble + verify SHA-256 + write. Any I/O failure (disk full, perms) is
                // caught here so it becomes a Failed notification instead of tearing down dispatch.
                FileReceiveResult result;
                try
                {
                    result = _fileReceiver.OnComplete(comp);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[File] Save failed for transfer {Id}", comp.TransferId);
                    result = new FileReceiveResult(comp.FileName, null, false, 0, ex.Message);
                }

                if (result.Ok)
                    _logger.LogInformation("[File] SAVED '{Name}' ({Size} bytes) → {Path}",
                        result.FileName, result.SizeBytes, result.SavedPath);
                else
                    _logger.LogWarning("[File] Transfer FAILED '{Name}': {Err}", result.FileName, result.Error);

                await SendFileNotifyAsync(new FileReceivedNotifyMessage
                {
                    TransferId = comp.TransferId,
                    FileName = result.FileName,
                    SavedPath = result.SavedPath,
                    SizeBytes = result.SizeBytes,
                    Status = (byte)(result.Ok ? FileNotifyStatus.Saved : FileNotifyStatus.Failed),
                    Error = result.Error,
                }, ct);
                break;
            }

            // ─── Breakout rooms (state tracking) ───
            case MessageType.BreakoutAssign:
                if (!IsForMe(env)) return;
                // TODO: track _myRoomId for IsForMe group routing
                _logger.LogInformation("[Dispatch] BreakoutAssign received");
                await ForwardToAgentAsync(env, ct);
                break;

            // ─── Everything else → forward to Agent ───
            default:
                if (!IsForMe(env)) return;
                _logger.LogDebug("[Dispatch] Forwarding unhandled type {Type} to Agent", env.Type);
                await ForwardToAgentAsync(env, ct);
                break;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Routing — envelope targeting
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Checks whether an envelope is addressed to this student endpoint.
    /// Mirrors the Windows ClassroomWorker.IsForMe logic (broadcast + targeted + group).
    /// </summary>
    private bool IsForMe(Envelope env)
    {
        // Explicit target to this endpoint
        if (env.TargetEndpointId == _endpointId) return true;

        // TODO: group routing — check env.TargetGroupId against _myRoomId
        // if (env.TargetGroupId.HasValue && _myRoomId.HasValue
        //     && env.TargetGroupId.Value == _myRoomId.Value) return true;

        // Broadcast (no target, no group)
        if (env.TargetEndpointId == Guid.Empty && !env.TargetGroupId.HasValue) return true;

        return false;
    }

    // ════════════════════════════════════════════════════════════════════
    //  IPC — write to Agent
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Forward an envelope to the connected Agent UI via the Unix Domain Socket.
    /// Length-prefixed frame, big-endian (same framing as Windows IPC).
    /// </summary>
    private async Task ForwardToAgentAsync(Envelope env, CancellationToken ct)
    {
        var socket = _agentSocket;
        if (socket is null || !socket.Connected)
        {
            _logger.LogDebug(
                "[IPC] Service→Agent dropped (no Agent connected): type={Type}", env.Type);
            return;
        }

        var body = env.Serialize();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);

        await _agentWriteLock.WaitAsync(ct);
        try
        {
            using var stream = new NetworkStream(socket, ownsSocket: false);
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[IPC] Service→Agent write failed: type={Type}", env.Type);
        }
        finally
        {
            _agentWriteLock.Release();
        }
    }

    /// <summary>
    /// Send a lightweight file-transfer notification to the Agent UI. Best-effort by design:
    /// the file is already saved by the daemon, so a missing/closed Agent simply means no toast
    /// (<see cref="ForwardToAgentAsync"/> no-ops when no Agent is connected).
    /// </summary>
    private Task SendFileNotifyAsync(FileReceivedNotifyMessage notify, CancellationToken ct)
    {
        var bytes = MessagePackSerializer.Serialize(notify);
        var env = Envelope.Create(MessageType.FileReceivedNotify, bytes, _endpointId);
        return ForwardToAgentAsync(env, ct);
    }

    // ════════════════════════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Deserialize a <see cref="PolicyApplyMessage"/> from a <c>PolicyApply</c> payload.
    /// Returns null on empty/corrupt payload (logged) so the caller can safely skip enforcement.
    /// </summary>
    private PolicyApplyMessage? DecodePolicy(byte[] payload)
    {
        if (payload.Length == 0)
        {
            _logger.LogWarning("[Dispatch] PolicyApply with empty payload — ignored");
            return null;
        }
        try
        {
            return MessagePackSerializer.Deserialize<PolicyApplyMessage>(payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Dispatch] Failed to deserialize PolicyApplyMessage ({Len} bytes)", payload.Length);
            return null;
        }
    }

    /// <summary>Read exactly <paramref name="buffer"/>.Length bytes from the stream.
    /// Returns false on EOF (connection closed).</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n == 0) return false;   // EOF
            total += n;
        }
        return true;
    }

    /// <summary>
    /// Load the persisted endpoint ID from disk, or generate a new one on first run.
    /// Stored as a simple JSON file so the Teacher recognizes this student across restarts.
    /// </summary>
    private Guid LoadOrCreateEndpointId()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "NTY", "ClassroomCtrl");
        var idFile = Path.Combine(configDir, "endpoint-id.txt");

        try
        {
            if (File.Exists(idFile))
            {
                var text = File.ReadAllText(idFile).Trim();
                if (Guid.TryParse(text, out var existing))
                {
                    _logger.LogInformation("[Init] Loaded endpoint ID: {Id}", existing);
                    return existing;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Init] Failed to load endpoint ID — generating new");
        }

        var newId = Guid.NewGuid();
        try
        {
            Directory.CreateDirectory(configDir);
            File.WriteAllText(idFile, newId.ToString());
            _logger.LogInformation("[Init] Generated new endpoint ID: {Id}", newId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Init] Failed to persist endpoint ID (will change on next restart)");
        }

        return newId;
    }

    /// <summary>Log network interfaces at startup for diagnostics (same as Windows worker).</summary>
    private void LogNetworkInterfaces()
    {
        try
        {
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var ipv4s = nic.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToArray();
                if (ipv4s.Length == 0) continue;
                _logger.LogInformation(
                    "[Init] NIC: Name='{Name}' Type={Type} IPv4=[{Addrs}]",
                    nic.Name, nic.NetworkInterfaceType, string.Join(",", ipv4s));
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Init] Failed to enumerate NICs"); }
    }

    private void CleanupIpcSocket()
    {
        try { _listenSocket?.Dispose(); } catch { }
        try { _agentSocket?.Dispose(); } catch { }
        try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch { }
    }
}

// ════════════════════════════════════════════════════════════════════════
//  Lock screen command payload (simple message for optional custom text)
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Optional payload for <see cref="MessageType.LockScreen"/>.
/// When present, carries the custom message the teacher set for the lock screen.
/// When absent or empty, the lock shows the default "Locked by teacher" text.
/// </summary>
[MessagePack.MessagePackObject]
public class LockScreenCommandMessage
{
    [MessagePack.Key(0)] public string Message { get; set; } = "";
}
