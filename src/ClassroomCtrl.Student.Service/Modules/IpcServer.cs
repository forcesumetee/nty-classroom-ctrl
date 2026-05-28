using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;

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
                stream = CreatePipeServer();
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

    /// <summary>
    /// Phase 10.19 — create the IPC pipe server with an explicit security descriptor
    /// so the non-elevated Agent (Medium integrity) can connect even when this Service
    /// is running elevated (High integrity).
    ///
    /// Without the explicit descriptor:
    ///   - The pipe inherits the Service's High mandatory integrity label.
    ///   - Windows' mandatory integrity check (which runs before DACL evaluation)
    ///     rejects any Medium-IL client trying to open it.
    ///   - The Agent's NamedPipeClientStream.Connect throws
    ///     UnauthorizedAccessException, no command frames flow either way, and
    ///     teacher-side "View Full Screen" shows black.
    ///
    /// The SDDL has two parts:
    ///   D: (DACL) — who is allowed to open the pipe:
    ///       SYSTEM, Administrators: FullControl (FA).
    ///       Authenticated Users: ReadWrite + CreateNewInstance + Synchronize
    ///         (0x12019F = sum of PipeAccessRights ReadData|WriteData|
    ///         CreateNewInstance|ReadEA|WriteEA|ReadAttributes|WriteAttributes|
    ///         ReadPermissions|Synchronize).  Wide enough for the Agent's
    ///         length-prefixed async read/write loop; no permission-change or
    ///         delete rights are granted to non-admins.
    ///   S: (SACL) — mandatory integrity label:
    ///       ML;NW;ME = SYSTEM_MANDATORY_LABEL, NO_WRITE_UP, MEDIUM.
    ///       This lowers the pipe's IL from the Service's High inheritance to
    ///       Medium, so the kernel's integrity check passes for the Medium Agent.
    ///
    /// Fallback path: setting a mandatory label requires SeSecurityPrivilege which
    /// only an elevated process holds.  If Create with the full descriptor throws
    /// (typical on a dev box where the Service runs non-elevated), retry with a
    /// DACL-only descriptor.  In that case both Service and Agent already run at
    /// the same IL (Medium), so the kernel's mandatory check passes without an
    /// explicit label and the DACL grant alone is sufficient.
    ///
    /// We do NOT ship "Agent runs as administrator" as the fix: many student PCs
    /// are standard-user accounts with no admin rights at all, and elevating a
    /// tray/UI app breaks shell drag-drop.  This descriptor is the standard
    /// solution for a privileged service IPC-ing with a desktop app.
    /// </summary>
    private NamedPipeServerStream CreatePipeServer()
    {
        // DACL ACEs (same in both code paths):
        //   A;FA;SY = LocalSystem        — FullControl
        //   A;FA;BA = BUILTIN\Admins     — FullControl
        //   A;0x12019F;AU = Authenticated Users — ReadWrite + CreateNewInstance
        //                                         + Synchronize + ReadPermissions
        const string dacl = "D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x12019F;;;AU)";
        // Mandatory label SACL: ML;NW;ME = Medium IL, no-write-up.
        const string sacl = "S:(ML;;NW;;;ME)";

        try
        {
            return CreatePipeWithSddl(dacl + sacl);
        }
        catch (System.IO.IOException ex)
        {
            // Expected when Service is not elevated.  See class doc above for why
            // the fallback is safe in that scenario.
            _logger.LogWarning(ex,
                "Pipe creation with Medium mandatory-label SACL failed; retrying " +
                "with DACL only.  (This is expected when the Service is NOT elevated. " +
                "If you see this in production where the Service IS elevated, the " +
                "Agent may still fail to connect from Medium IL.)");
            return CreatePipeWithSddl(dacl);
        }
    }

    private static NamedPipeServerStream CreatePipeWithSddl(string sddl)
    {
        var ps = new PipeSecurity();
        ps.SetSecurityDescriptorSddlForm(sddl);
        return NamedPipeServerStreamAcl.Create(
            pipeName: NetworkConstants.IpcPipeName,
            direction: PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: ps);
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