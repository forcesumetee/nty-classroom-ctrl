using ClassroomCtrl.Shared.Protocol;
using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Named-pipe client. Connects to Service (\\.\pipe\ClassroomCtrl),
/// reads forwarded Envelope messages, and can send Envelope messages back to Service.
///
/// Phase 10.12 — fixed startup-race + reconnect-after-drop bugs.  The previous
/// implementation had a retry loop on the surface (an outer <c>while</c> with a
/// 2-second <c>Task.Delay</c>), but the read pump used <c>return;</c> on pipe EOF,
/// which exited <c>RunLoop</c> entirely on the very first pipe drop and left the
/// IpcClient permanently dead.  In production this manifested as: Service
/// crashes / restarts → Agent never reconnects → every Service↔Agent forward
/// fails with "(pipe null)" and the only recovery was a manual Agent restart.
///
/// Three corrections are layered on top:
///  • Bug fix: read-loop exit uses <c>break</c>/throw so the outer reconnect
///    loop sees the drop and retries (was <c>return;</c>).
///  • Exponential backoff (1 s → 2 s → 4 s → 8 s → 16 s → 30 s, capped) instead
///    of a fixed 2 s wait — same shape as <see cref="Service.ClassroomWorker"/>'s
///    Teacher-side reconnect loop in Phase 10.10 Fix 6.
///  • Explicit connect timeout via linked CTS so a long-stuck connect surfaces
///    as <see cref="TimeoutException"/> immediately and triggers backoff
///    (matches the ClassroomWorker pattern).
/// </summary>
public class IpcClient
{
    /// <summary>Initial backoff after a failed connect or a dropped pipe.</summary>
    private const int InitialBackoffMs = 1_000;
    /// <summary>Backoff cap.  After this many milliseconds we stop doubling and
    /// just retry every 30 s until the user-cancellation-token cancels.</summary>
    private const int MaxBackoffMs = 30_000;
    /// <summary>How long to wait for a single pipe connect to succeed.  Local
    /// IPC connects in &lt;10 ms when the server is up; 2 s gives a comfortable
    /// margin for OS scheduling jitter and CPU pressure during boot, while
    /// still cycling through retries every few seconds when the server is down.</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

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

    /// <summary>
    /// Phase 10.12 — connect/reconnect loop with exponential backoff.  Runs for
    /// the entire lifetime of the Agent process; only exits when
    /// <paramref name="ct"/> is cancelled (typically on Agent shutdown).
    /// </summary>
    private async Task RunLoop(CancellationToken ct)
    {
        int backoffMs = InitialBackoffMs;
        bool everConnected = false;

        while (!ct.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            bool wasConnectedThisIteration = false;
            try
            {
                pipe = new NamedPipeClientStream(".",
                    NetworkConstants.IpcPipeName,
                    PipeDirection.InOut, PipeOptions.Asynchronous);

                LogToFile(everConnected
                    ? $"[IpcClient] IPC reconnecting to pipe \\\\.\\pipe\\{NetworkConstants.IpcPipeName}..."
                    : $"[IpcClient] IPC connecting to pipe \\\\.\\pipe\\{NetworkConstants.IpcPipeName}...");

                // Phase 10.12 — explicit connect timeout via linked CTS.  The
                // built-in ConnectAsync(int timeout, CancellationToken) overload
                // throws TimeoutException directly, but the explicit pattern
                // matches ClassroomWorker (Teacher TCP connect) so future
                // maintainers see one consistent shape across the codebase.
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    connectCts.CancelAfter(ConnectTimeout);
                    try
                    {
                        await pipe.ConnectAsync(connectCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"IPC pipe connect timed out after {ConnectTimeout.TotalSeconds}s");
                    }
                }

                LogToFile("[IpcClient] IPC connected to Service");
                _activePipe = pipe;
                everConnected = true;
                wasConnectedThisIteration = true;
                backoffMs = InitialBackoffMs;   // reset on each successful connect
                ConnectionStateChanged?.Invoke(this, true);

                // Phase 10.12 — read pump runs until pipe EOF or read failure.
                // PumpReadAsync returns normally on EOF (no exception) so we
                // can fall through to the "dropped; will reconnect" log without
                // tripping the outer catch.  Only true exceptions land in the
                // inner catch below.
                try
                {
                    await PumpReadAsync(pipe, ct);
                    LogToFile("[IpcClient] IPC connection dropped (pipe EOF); will reconnect");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogToFile(
                        $"[IpcClient] IPC connection dropped: {ex.GetType().Name}: {ex.Message}; will reconnect");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Connect-phase failure (pipe server not yet listening,
                // ACL denial, TimeoutException from our linked CTS, …).
                // Do not log "dropped" — we never connected this iteration.
                LogToFile(
                    $"[IpcClient] IPC connect failed: {ex.GetType().Name}: {ex.Message}; retry in {backoffMs} ms");
            }
            finally
            {
                _activePipe = null;
                try { pipe?.Dispose(); } catch { /* best-effort */ }
                if (wasConnectedThisIteration)
                    ConnectionStateChanged?.Invoke(this, false);
            }

            // Phase 10.12 — exponential backoff, capped at 30 s.  Reset
            // happens on each successful connect (above), so a healthy
            // connection that drops after hours starts the next attempt
            // at 1 s rather than wherever it had drifted to.
            try { await Task.Delay(backoffMs, ct); }
            catch (OperationCanceledException) { break; }
            backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
        }
    }

    /// <summary>
    /// Phase 10.12 — read pump split out of <see cref="RunLoop"/> so EOF can
    /// return without short-circuiting the outer reconnect loop.  The previous
    /// implementation used <c>return</c> from inside RunLoop's try block, which
    /// terminated the entire RunLoop and was the single root cause of the
    /// "Agent never reconnects after Service restart" bug.
    /// </summary>
    private async Task PumpReadAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
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
            if (len <= 0 || len > 16 * 1024 * 1024)
            {
                LogToFile($"[IpcClient] Bad frame length {len}; closing pipe");
                return;
            }

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

    /// <summary>
    /// Send an Envelope message to the Service (Agent → Service direction).
    /// Used by hand-raise, chat send-up, etc.
    /// Thread-safe via <see cref="_writeLock"/>; multiple UI/background
    /// threads may call concurrently.
    /// </summary>
    public async Task SendAsync(Envelope env, CancellationToken ct = default)
    {
        var pipe = _activePipe;
        if (pipe is null || !pipe.IsConnected)
        {
            // Phase 10.12 — silent drop here used to be the visible failure
            // mode reported by users.  With the reconnect loop fixed, this
            // path now means either:
            //   (a) we're between reconnect attempts (transient, &lt;30 s)
            //   (b) the Service has been killed and not yet restarted
            // Either way the caller's message is lost — there is no outbox
            // queue.  That matches the pre-Phase-10.12 contract; building an
            // outbox is a separate phase if/when needed.
            LogToFile($"[IpcClient] SendAsync: pipe not connected; dropping type={env.Type}");
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
