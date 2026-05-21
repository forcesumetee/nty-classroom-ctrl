using ClassroomCtrl.Shared.Discovery;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Networking.Discovery;

/// <summary>
/// Phase 9.4: Listens on UDP 7778 for the teacher's discovery beacon.
///
/// Phase 10.10 — Fix 5 split this from a static helper into an instance class
/// holding a long-lived UdpClient.  Two reasons:
///  1. Open/close on every WaitForBeaconAsync was thrashing the OS socket
///     table — bind/unbind storms during retry loops occasionally raced with
///     anti-virus filter drivers and produced sporadic "Address already in
///     use" / "WSAENOTSOCK" errors.
///  2. With <see cref="UdpClient.ExclusiveAddressUse"/> = false plus
///     <see cref="SocketOptionName.ReuseAddress"/> = true, multiple processes
///     can bind the same UDP port (handy for dev side-by-side testing of two
///     student instances on one machine, and harmless in production because
///     beacon traffic is broadcast — every binder receives every packet).
///
/// The static <c>WaitForBeaconAsync</c> remains as a backward-compat wrapper
/// over a private singleton instance so callers that aren't ready to take a
/// dispose-able dependency keep working.  New code should construct an
/// instance and reuse it.
/// </summary>
public class TeacherDiscoveryClient : IDisposable
{
    private const int BeaconPort = 7778;

    private readonly UdpClient _udp;
    private bool _disposed;

    public TeacherDiscoveryClient()
    {
        // Fix 5 — open the underlying socket manually so we can flip the two
        // socket options before Bind().  UdpClient(int port) constructs and
        // binds in a single step with ExclusiveAddressUse=true (the default),
        // which is what we're trying to avoid.
        _udp = new UdpClient();
        try
        {
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.ExclusiveAddressUse = false;
        }
        catch { /* best-effort — older Windows might reject the SetSocketOption call */ }
        _udp.EnableBroadcast = true;
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, BeaconPort));
    }

    /// <summary>
    /// Listens for at most <paramref name="timeoutMs"/> milliseconds. Returns the first
    /// beacon whose ChannelId matches <paramref name="channelId"/>, or null on timeout.
    /// Safe to call repeatedly on the same instance.
    ///
    /// Renamed from <c>WaitForBeaconAsync</c> to avoid colliding with the static
    /// backward-compat wrapper of the same name; the static method delegates here
    /// via a process-wide singleton instance.
    /// </summary>
    public async Task<IPEndPoint?> WaitOnceAsync(string channelId, int timeoutMs, CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TeacherDiscoveryClient));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult result;
                try { result = await _udp.ReceiveAsync(cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
                catch (SocketException) { return null; }

                try
                {
                    var payload = MessagePack.MessagePackSerializer.Deserialize<BeaconPayload>(result.Buffer);
                    if (string.Equals(payload.ChannelId, channelId, StringComparison.OrdinalIgnoreCase)
                        && IPAddress.TryParse(payload.TeacherIp, out var ip))
                    {
                        return new IPEndPoint(ip, payload.TcpPort);
                    }
                }
                catch { /* skip malformed beacons */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }

        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _udp.Dispose(); } catch { }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Backward-compat surface — Fix 3 + Fix 5
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fix 3 — registry helpers now route through <see cref="ChannelIdRegistry"/>
    /// so Teacher and Student.Service (LocalSystem) read the same value.  Kept as
    /// static methods for source-level backward compat with callers that haven't
    /// migrated yet.
    /// </summary>
    public static string ReadStudentChannelId() => ChannelIdRegistry.Read();

    /// <summary>Fix 3 — see <see cref="ReadStudentChannelId"/>.</summary>
    public static void WriteStudentChannelId(string id) => ChannelIdRegistry.Write(id);

    private static readonly Lazy<TeacherDiscoveryClient> _shared =
        new(() => new TeacherDiscoveryClient(), isThreadSafe: true);

    /// <summary>
    /// Fix 5 — backward-compat wrapper.  Old callers that did
    /// <c>TeacherDiscoveryClient.WaitForBeaconAsync(...)</c> as a static call
    /// reuse a process-wide singleton instance under the hood, so they get the
    /// long-lived-socket benefits without source changes.  New callers should
    /// construct + dispose their own instance.
    /// </summary>
    public static Task<IPEndPoint?> WaitForBeaconAsync(string channelId, int timeoutMs, CancellationToken ct)
        => _shared.Value.WaitOnceAsync(channelId, timeoutMs, ct);
}
