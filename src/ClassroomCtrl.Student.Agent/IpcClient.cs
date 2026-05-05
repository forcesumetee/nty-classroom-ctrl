using ClassroomCtrl.Shared.Protocol;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Named-pipe client. Connects to Service (\\.\pipe\ClassroomCtrl),
/// reads forwarded Envelope messages, and can send Envelope messages back to Service.
/// </summary>
public class IpcClient
{
    public event EventHandler<Envelope>? MessageReceived;
    public event EventHandler<bool>? ConnectionStateChanged;

    private CancellationTokenSource? _cts;
    private NamedPipeClientStream? _activePipe;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => RunLoop(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".",
                    NetworkConstants.IpcPipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);

                LogToFile("[IpcClient] Connecting to pipe...");
                await pipe.ConnectAsync(5000, ct);
                LogToFile("[IpcClient] Connected to pipe!");
                _activePipe = pipe;
                ConnectionStateChanged?.Invoke(this, true);

                var lengthBuf = new byte[4];
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    int total = 0;
                    while (total < 4)
                    {
                        int n = await pipe.ReadAsync(lengthBuf.AsMemory(total, 4 - total), ct);
                        if (n == 0)
                        {
                            LogToFile("[IpcClient] Pipe EOF (length read)");
                            return;
                        }
                        total += n;
                    }
                    int len = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
                    LogToFile($"[IpcClient] Frame length={len}");
                    if (len <= 0 || len > 16 * 1024 * 1024) break;

                    var payload = new byte[len];
                    total = 0;
                    while (total < len)
                    {
                        int n = await pipe.ReadAsync(payload.AsMemory(total, len - total), ct);
                        if (n == 0)
                        {
                            LogToFile("[IpcClient] Pipe EOF (body read)");
                            return;
                        }
                        total += n;
                    }
                    var env = Envelope.Deserialize(payload);
                    LogToFile($"[IpcClient] Received: type={env.Type}, payload={payload.Length} bytes");
                    MessageReceived?.Invoke(this, env);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogToFile($"[IpcClient] Error: {ex.Message}");
            }
            finally
            {
                _activePipe = null;
                ConnectionStateChanged?.Invoke(this, false);
            }
            await Task.Delay(2000, ct);
        }
    }

    /// <summary>
    /// Send an Envelope message to the Service (Agent → Service direction).
    /// Used by hand-raise, chat send-up, etc.
    /// </summary>
    public async Task SendAsync(Envelope env, CancellationToken ct = default)
    {
        var pipe = _activePipe;
        if (pipe is null || !pipe.IsConnected)
        {
            LogToFile("[IpcClient] SendAsync: pipe not connected");
            return;
        }

        var body = env.Serialize();
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);

        await _writeLock.WaitAsync(ct);
        try
        {
            await pipe.WriteAsync(len, ct);
            await pipe.WriteAsync(body, ct);
            await pipe.FlushAsync(ct);
            LogToFile($"[IpcClient] Sent: type={env.Type}, {body.Length} bytes");
        }
        catch (Exception ex)
        {
            LogToFile($"[IpcClient] SendAsync error: {ex.Message}");
        }
        finally { _writeLock.Release(); }
    }

    private static readonly object _logLock = new();
    public static void LogToFile(string msg)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "agent-debug.log");
            lock (_logLock)
            {
                File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
        }
        catch { }
    }
}