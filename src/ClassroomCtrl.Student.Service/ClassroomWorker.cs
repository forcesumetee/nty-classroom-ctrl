using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Student.Service.Modules;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;

namespace ClassroomCtrl.Student.Service;

public class ClassroomWorker : BackgroundService
{
    private readonly ILogger<ClassroomWorker> _logger;
    private readonly IpcServer _ipc;
    private readonly PolicyEnforcer _policy;
    private readonly ScreenLocker _locker;
    private readonly FileReceiver _fileReceiver;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Guid _endpointId;
    private readonly SemaphoreSlim _teacherWriteLock = new(1, 1);

    private IPEndPoint? _teacherEndpoint;
    private NetworkStream? _teacherStream;

    // Phase 10.13 — remember last beacon endpoint to log only on actual IP change,
    // instead of every 30s beacon iteration (was flooding student log).
    private IPEndPoint? _lastBeaconEndpoint;

    public ClassroomWorker(
        ILogger<ClassroomWorker> logger, ILoggerFactory loggerFactory,
        IpcServer ipc, PolicyEnforcer policy,
        ScreenLocker locker, FileReceiver fileReceiver)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _ipc = ipc;
        _policy = policy;
        _locker = locker;
        _fileReceiver = fileReceiver;
        _endpointId = LoadOrCreateEndpointId();

        _ipc.AgentMessageReceived += OnAgentMessage;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        _logger.LogInformation("ClassroomService starting (endpoint {Id})", _endpointId);

        // Phase 10.8 — log session/identity context up front.  Console-mode runs
        // (UserInteractive=true, SessionId=1+, MachineName\<user>) and Service-mode
        // runs (UserInteractive=false, SessionId=0, NT AUTHORITY\SYSTEM) take very
        // different code paths inside Windows for pipe ACLs, network namespace,
        // multicast joins, etc.  Capturing this once at startup makes the bug
        // tractable — every other "why does this work in console but not service"
        // question can be answered by reading these four lines.
        try
        {
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            _logger.LogInformation(
                "Process: PID={Pid} SessionId={Session} UserInteractive={Interactive} User={User}",
                proc.Id, proc.SessionId,
                Environment.UserInteractive,
                $"{Environment.UserDomainName}\\{Environment.UserName}");
            _logger.LogInformation("Host: Machine={Machine} OS={OS} CWD={Cwd}",
                Environment.MachineName, Environment.OSVersion.VersionString,
                Environment.CurrentDirectory);
            // List network adapters with IPv4 addresses so we can compare what
            // SYSTEM (Session 0) sees vs what the user session sees.  In some
            // VPN/RDP/NIC-teaming setups the two views differ, which would
            // explain a TCP send that succeeds but never lands at Teacher.
            foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var ipv4s = nic.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.Address.ToString())
                    .ToArray();
                if (ipv4s.Length == 0) continue;
                _logger.LogInformation("NIC: Name='{Name}' Type={Type} IPv4=[{Addrs}]",
                    nic.Name, nic.NetworkInterfaceType, string.Join(",", ipv4s));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to log startup environment");
        }

        // Phase 10.10 Fix 4 — install Student firewall rules before any networking.
        // Idempotent (no-op when rules already present).  We're either LocalSystem
        // (Windows Service mode) or running with HighestAvailable via the Phase 10.9
        // scheduled task; both have netsh privilege without a UAC prompt.
        StudentFirewallService.EnsureRules(_logger);

        // Phase 10.3 — Service no longer spawns Agent/Watchdog.  Agent runs in
        // the user session via HKLM Run autorun and connects back here through
        // the named-pipe IPC.  Spawning from Session 0 produced a respawn loop
        // (WPF needs a desktop, Agent crashed, supervisor restarted, repeat).
        await _ipc.StartAsync(ct);
        _logger.LogInformation("IPC server listening on \\\\.\\pipe\\{Pipe}; waiting for Agent connection.",
            NetworkConstants.IpcPipeName);

        _teacherEndpoint = ClassroomCtrl.Networking.TeacherIPConfig.GetEndpoint();
        var manualConfigured = ClassroomCtrl.Networking.TeacherIPConfig.IsConfigured();
        _logger.LogInformation("Teacher endpoint: {Endpoint} (configured: {Configured})",
            _teacherEndpoint, manualConfigured);

        int backoffMs = 1000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Phase 9.4: Auto-discover via UDP beacon when no manual IP is configured.
                if (!manualConfigured)
                {
                    var channel = ClassroomCtrl.Networking.Discovery.TeacherDiscoveryClient.ReadStudentChannelId();
                    _logger.LogInformation("Listening for beacon on channel {Channel}...", channel);
                    var found = await ClassroomCtrl.Networking.Discovery.TeacherDiscoveryClient
                        .WaitForBeaconAsync(channel, timeoutMs: 30000, ct);
                    if (found != null)
                    {
                        _teacherEndpoint = found;
                        // Phase 10.13 — log only on actual IP change so customer-side debug
                        // is tractable when Teacher's DHCP lease changes or they switch NIC.
                        if (_lastBeaconEndpoint == null)
                        {
                            _logger.LogInformation("Beacon discovered teacher at {Endpoint}", found);
                        }
                        else if (!_lastBeaconEndpoint.Equals(found))
                        {
                            _logger.LogInformation("Teacher IP changed: {Old} -> {New} (will reconnect)",
                                _lastBeaconEndpoint, found);
                        }
                        _lastBeaconEndpoint = found;
                    }
                }

                if (_teacherEndpoint != null)
                {
                    await ConnectAndPumpAsync(_teacherEndpoint, ct);
                    backoffMs = 1000;
                }
                else
                {
                    await Task.Delay(2000, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Connection failed: {Msg}; retry in {Ms} ms", ex.Message, backoffMs);
                await Task.Delay(backoffMs, ct);
                backoffMs = Math.Min(backoffMs * 2, 30000);
            }
        }
    }

    private async Task ConnectAndPumpAsync(IPEndPoint? ep, CancellationToken ct)
    {
        if (ep == null) return;
        using var client = new TcpClient();

        // Phase 10.10 Fix 6 — bound the connect attempt at 5 s.  Without this,
        // a wrong / unreachable Teacher IP would block on the OS SYN timeout
        // (~21 s) before each retry, multiplied by exponential backoff —
        // students could appear "frozen" for over a minute waiting on a stale
        // address.  Using a linked CTS turns the TCP connect into an explicit
        // TimeoutException so the outer catch routes through the existing
        // backoff path.
        const int ConnectTimeoutMs = 5000;
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connectCts.CancelAfter(ConnectTimeoutMs);
        try
        {
            await client.ConnectAsync(ep.Address, ep.Port, connectCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The outer ct wasn't cancelled — this means our timeout fired.
            throw new TimeoutException($"Connect to {ep} timed out after {ConnectTimeoutMs} ms");
        }
        _logger.LogInformation("Connected to Teacher at {Endpoint}", ep);

        var stream = client.GetStream();
        _teacherStream = stream;

        // Phase 10.10 Fix 7 — heartbeat loop runs alongside the read loop while
        // the TCP connection is live.  Sends an empty-payload Ping every 5 s so
        // a silent Wi-Fi drop on the student side becomes visible to Teacher
        // within ~15 s (StaleAfterMs in TcpControlServer).
        using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingTask = Task.Run(() => HeartbeatLoopAsync(pingCts.Token), pingCts.Token);
        try
        {
            var hello = new HelloMessage
            {
                MachineName = Environment.MachineName,
                DisplayName = Environment.UserName,
                OsVersion = Environment.OSVersion.VersionString,
                ProtocolVersion = NetworkConstants.ProtocolVersion,
                EndpointId = _endpointId,
            };
            var helloBytes = MessagePack.MessagePackSerializer.Serialize(hello);
            var env = Envelope.Create(MessageType.Hello, helloBytes, _endpointId);
            await SendToTeacherAsync(env, ct);

            var lengthBuf = new byte[4];
            while (!ct.IsCancellationRequested)
            {
                int read = await ReadExact(stream, lengthBuf, 4, ct);
                if (read == 0) break;
                int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
                var payload = new byte[len];
                await ReadExact(stream, payload, len, ct);
                var msg = Envelope.Deserialize(payload);

                // Phase 10.10 Fix 7 — Pong replies are silent (Teacher uses them
                // for liveness only; Student doesn't act on them).  Skip the
                // info-level log so steady-state traffic stays quiet.
                if (msg.Type != MessageType.Pong)
                {
                    _logger.LogInformation("Teacher→Service: type={Type} size={Size}", msg.Type, len);
                }
                await DispatchAsync(msg, ct);
            }
            _logger.LogInformation("Teacher TCP read loop ended (peer closed or read returned 0).");
        }
        finally
        {
            try { pingCts.Cancel(); } catch { }
            try { await pingTask.ConfigureAwait(false); } catch { /* expected on cancel */ }
            _teacherStream = null;
            _logger.LogInformation("Teacher connection closed; will retry per backoff loop.");
        }
    }

    /// <summary>
    /// Phase 10.10 Fix 7 — periodic Ping every 5 s while connected.  Fire-and-
    /// forget; transient send errors are logged but don't crash the worker —
    /// the read loop will detect a real disconnect and tear down.
    /// </summary>
    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { return; }

            try
            {
                var ping = Envelope.Create(MessageType.Ping, Array.Empty<byte>(), _endpointId);
                await SendToTeacherAsync(ping, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Heartbeat ping send failed (will keep trying)");
            }
        }
    }

    private async void OnAgentMessage(object? sender, Envelope env)
    {
        try
        {
            env.SenderId = _endpointId;
            await SendToTeacherAsync(env, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forward Agent → Teacher failed");
        }
    }

    private async Task SendToTeacherAsync(Envelope env, CancellationToken ct)
    {
        var s = _teacherStream;
        if (s is null)
        {
            // Phase 10.8 — silent drops here were one of the suspect paths.
            // Logging the type makes "Teacher saw Hello but no Frames" debuggable.
            _logger.LogWarning("Service→Teacher dropped (stream null): type={Type}", env.Type);
            return;
        }

        var body = env.Serialize();
        var len = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, body.Length);

        await _teacherWriteLock.WaitAsync(ct);
        try
        {
            await s.WriteAsync(len, ct);
            await s.WriteAsync(body, ct);
            await s.FlushAsync(ct);
            // Phase 10.10 Fix 7 — Ping is sent every 5 s; logging at Info would
            // bury everything else.  Demote to Debug for keepalive frames.
            if (env.Type == MessageType.Ping)
                _logger.LogDebug("Service→Teacher: Ping size={Size}", body.Length);
            else
                _logger.LogInformation("Service→Teacher: type={Type} size={Size}", env.Type, body.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Service→Teacher write failed: type={Type} size={Size}", env.Type, body.Length);
            throw;
        }
        finally { _teacherWriteLock.Release(); }
    }

    private bool IsForMe(Envelope env)
        => env.TargetEndpointId == Guid.Empty || env.TargetEndpointId == _endpointId;

    private async Task DispatchAsync(Envelope env, CancellationToken ct)
    {
        switch (env.Type)
        {
            // Phase 10.10 Fix 7 — heartbeat traffic is connection-level.  We
            // handle it here so the default-case Debug "Unhandled" log doesn't
            // fire on every Pong reply.  No further action required: the read
            // loop already updated the implicit liveness signal by virtue of
            // having received the frame.
            case MessageType.Ping:
            case MessageType.Pong:
                return;

            case MessageType.LockScreen:
            case MessageType.UnlockScreen:
                if (!IsForMe(env))
                {
                    _logger.LogDebug("{Type} not for me (target={Target})", env.Type, env.TargetEndpointId);
                    return;
                }
                _logger.LogInformation("{Type} command received from teacher", env.Type);
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 6: Power-state commands. Service runs as LocalSystem so it has
            // (or can self-grant) the privileges needed; PowerCommands handles the
            // mechanics (shutdown.exe / WTSLogoffSession).
            case MessageType.ForceShutdown:
                if (!IsForMe(env)) return;
                _logger.LogWarning("ForceShutdown from teacher (target={Target})", env.TargetEndpointId);
                PowerCommands.Shutdown(_logger);
                break;

            case MessageType.ForceRestart:
                if (!IsForMe(env)) return;
                _logger.LogWarning("ForceRestart from teacher (target={Target})", env.TargetEndpointId);
                PowerCommands.Reboot(_logger);
                break;

            case MessageType.ForceLogoff:
                if (!IsForMe(env)) return;
                _logger.LogWarning("ForceLogoff from teacher (target={Target})", env.TargetEndpointId);
                PowerCommands.LogoffActiveUsers(_logger);
                break;

            case MessageType.PolicyApply:
                {
                    var policy = MessagePack.MessagePackSerializer.Deserialize<PolicyApplyMessage>(env.Payload);
                    if (env.TargetEndpointId == Guid.Empty)
                        _policy.ApplyClass(policy);
                    else if (env.TargetEndpointId == _endpointId)
                        _policy.ApplyPerStudent(policy);
                }
                break;

            case MessageType.PolicyRevert:
                if (env.TargetEndpointId == Guid.Empty)
                    _policy.RevertClass();
                else if (env.TargetEndpointId == _endpointId)
                    _policy.RevertPerStudent();
                break;

            case MessageType.ChatBroadcast:
                _logger.LogInformation("Chat broadcast received");
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            case MessageType.ChatDirect:
                if (!IsForMe(env)) return;
                _logger.LogInformation("Direct chat received (target={Target})", env.TargetEndpointId);
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            case MessageType.ChatRoom:
                if (!IsForMe(env)) return;
                _logger.LogInformation("Room chat received");
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            case MessageType.BreakoutAssign:
                if (!IsForMe(env)) return;
                _logger.LogInformation("Breakout assign received (target={Target})", env.TargetEndpointId);
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 5b: PDPA notification — Teacher tells me they're recording my screen.
            case MessageType.StudentRecordingNotify:
                if (!IsForMe(env)) return;
                _logger.LogInformation("StudentRecordingNotify received (target={Target})", env.TargetEndpointId);
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 6.6: PDPA notification — Teacher tells me they captured my screen.
            case MessageType.StudentScreenshotNotify:
                if (!IsForMe(env)) return;
                _logger.LogInformation("StudentScreenshotNotify received (target={Target})", env.TargetEndpointId);
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            case MessageType.FileAnnounce:
                {
                    var ann = MessagePack.MessagePackSerializer.Deserialize<FileAnnounceMessage>(env.Payload);
                    _fileReceiver.Announce(ann);
                }
                break;

            case MessageType.FileChunk:
                {
                    var chunk = MessagePack.MessagePackSerializer.Deserialize<FileChunkMessage>(env.Payload);
                    _fileReceiver.Chunk(chunk);
                }
                break;

            case MessageType.FileComplete:
                {
                    var done = MessagePack.MessagePackSerializer.Deserialize<FileCompleteMessage>(env.Payload);
                    _fileReceiver.Complete(done);
                }
                break;

            case MessageType.RequestScreenshot:
            case MessageType.ScreenStreamStart:
            case MessageType.ScreenStreamFrame:
            case MessageType.ScreenStreamStop:
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 9.1: Student Demonstration — pure forwarder. Agent decides what to do
            // based on whether the source student is itself.
            case MessageType.DemoStart:
            case MessageType.DemoFrame:
            case MessageType.DemoStop:
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 9.2: Screen Pen overlay — pure forwarder
            case MessageType.DrawingStroke:
            case MessageType.DrawingClear:
            case MessageType.DrawingUndo:
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 9.5: Camera Broadcast
            case MessageType.CameraStart:
            case MessageType.CameraFrame:
            case MessageType.CameraStop:
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 9.6: Net Movie
            case MessageType.MoviePlay:
            case MessageType.MoviePause:
            case MessageType.MovieSeek:
            case MessageType.MovieStop:
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 6.5: Remote Control (targeted to me only)
            case MessageType.RemoteControlStart:
            case MessageType.RemoteControlEnd:
            case MessageType.RemoteMouseMove:
            case MessageType.RemoteMouseClick:
            case MessageType.RemoteMouseScroll:
            case MessageType.RemoteKey:
                if (!IsForMe(env)) return;
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 4.6: Mic Monitor (targeted)
            case MessageType.MicMonitorStart:
            case MessageType.MicMonitorStop:
                if (!IsForMe(env)) return;
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            case MessageType.StudentStreamStart:
            case MessageType.StudentStreamStop:
                if (!IsForMe(env)) return;
                _logger.LogInformation("{Type} from teacher (target={Target})", env.Type, env.TargetEndpointId);
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            case MessageType.AudioStreamStart:
            case MessageType.AudioStreamFrame:
            case MessageType.AudioStreamStop:
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            case MessageType.ForceMuteStudentMic:
                _logger.LogInformation("ForceMuteStudentMic from teacher");
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            // Phase 13: Exam System — Service is a pure forwarder; payloads are
            // deserialized only by the Agent (which links to ClassroomCtrl.Exam.Shared).
            case MessageType.QuizStart:
                _logger.LogInformation("QuizStart received from teacher");
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            case MessageType.QuizEnd:
                _logger.LogInformation("QuizEnd from teacher");
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            case MessageType.QuizUnlockEarly:
                if (!IsForMe(env)) return;
                _logger.LogInformation("QuizUnlockEarly from teacher (target={Target})", env.TargetEndpointId);
                await _ipc.ForwardToAgentAsync(env, ct);
                break;

            default:
                _logger.LogDebug("Unhandled message type {Type}", env.Type);
                break;
        }
    }

    private static async Task<int> ReadExact(NetworkStream s, byte[] buf, int n, CancellationToken ct)
    {
        int read = 0;
        while (read < n)
        {
            int r = await s.ReadAsync(buf.AsMemory(read, n - read), ct);
            if (r == 0) return read;
            read += r;
        }
        return read;
    }

    private static Guid LoadOrCreateEndpointId() => Guid.NewGuid();
}