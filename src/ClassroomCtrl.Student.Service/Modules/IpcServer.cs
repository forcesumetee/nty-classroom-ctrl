using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO.Pipes;

namespace ClassroomCtrl.Student.Service.Modules;

/// <summary>
/// Named-pipe server for Service ↔ Agent IPC (Spec §3.3, §5.4).
/// Pipe path: \\.\pipe\ClassroomCtrl
/// </summary>
public class IpcServer
{
    private readonly ILogger<IpcServer> _logger;
    private NamedPipeServerStream? _agentStream;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Fired when an Agent-originated message arrives through the pipe.
    /// Subscribers (e.g., ClassroomWorker) forward these up to the Teacher over TCP.
    /// </summary>
    public event EventHandler<Envelope>? AgentMessageReceived;

    public IpcServer(ILogger<IpcServer> logger) => _logger = logger;

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? stream = null;
            try
            {
                stream = new NamedPipeServerStream(NetworkConstants.IpcPipeName,
                    PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await stream.WaitForConnectionAsync(ct);
                // Phase 10.8 — capture the impersonated client identity.  In Session-0
                // Service mode this should report the interactive user (e.g.
                // PC01\\Student) which proves the cross-session connect succeeded.
                string clientUser = "(unknown)";
                try
                {
                    stream.RunAsClient(() =>
                    {
                        clientUser = System.Security.Principal.WindowsIdentity
                            .GetCurrent().Name;
                    });
                }
                catch (Exception idEx)
                {
                    clientUser = "(impersonation failed: " + idEx.Message + ")";
                }
                _logger.LogInformation("Agent connected via IPC (client identity={User})", clientUser);
                _agentStream = stream;
                _ = Task.Run(() => HandleClient(stream, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IPC accept error");
                stream?.Dispose();
            }
        }
    }

    private async Task HandleClient(NamedPipeServerStream stream, CancellationToken ct)
    {
        try
        {
            var lengthBuf = new byte[4];
            while (!ct.IsCancellationRequested && stream.IsConnected)
            {
                int total = 0;
                while (total < 4)
                {
                    int n = await stream.ReadAsync(lengthBuf.AsMemory(total, 4 - total), ct);
                    if (n == 0) return;
                    total += n;
                }
                int len = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
                if (len <= 0 || len > 16 * 1024 * 1024) break;

                var payload = new byte[len];
                total = 0;
                while (total < len)
                {
                    int n = await stream.ReadAsync(payload.AsMemory(total, len - total), ct);
                    if (n == 0) return;
                    total += n;
                }

                try
                {
                    var env = Envelope.Deserialize(payload);
                    _logger.LogInformation("Agent→Service (IPC): type={Type} size={Size}", env.Type, len);
                    AgentMessageReceived?.Invoke(this, env);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize IPC payload from Agent");
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "IPC client error"); }
        finally
        {
            if (_agentStream == stream) _agentStream = null;
            stream.Dispose();
        }
    }

    /// <summary>
    /// Forward a Teacher-originated message down to the Agent UI.
    /// Length-prefixed frame, big-endian.
    /// </summary>
    public async Task ForwardToAgentAsync(Envelope env, CancellationToken ct)
    {
        var stream = _agentStream;
        if (stream is null || !stream.IsConnected)
        {
            // Phase 10.8 — silent drops here were one of the suspect paths in the
            // Service-mode-only regression.  When this fires it means a Teacher
            // command arrived but the Agent isn't connected yet (boot race) or
            // its pipe just dropped.  Without this log the failure was invisible.
            _logger.LogWarning(
                "Service→Agent (IPC) dropped (pipe {State}): type={Type}",
                stream is null ? "null" : "disconnected", env.Type);
            return;
        }

        var body = env.Serialize();
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);

        await _writeLock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(len, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
            _logger.LogInformation("Service→Agent (IPC): type={Type} size={Size}", env.Type, body.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Service→Agent (IPC) write failed: type={Type} size={Size}",
                env.Type, body.Length);
        }
        finally { _writeLock.Release(); }
    }
}