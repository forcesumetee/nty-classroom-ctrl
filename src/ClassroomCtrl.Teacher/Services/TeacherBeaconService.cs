using ClassroomCtrl.Networking;
using ClassroomCtrl.Shared.Discovery;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 9.4: Broadcasts a small UDP beacon (port 7778) every 2 seconds so
/// student agents on the same LAN can auto-discover this teacher by ChannelId.
///
/// Phase 10.10 — three deployment-stability fixes are layered on top:
///   * Fix 1 (GetLocalIp fallback): probe 8.8.8.8 → 1.1.1.1 → enumerate NICs
///     with a default gateway.  Never returns "0.0.0.0" — schools that block
///     outbound UDP to public DNS used to land here and broadcast a useless
///     beacon that students could parse but not connect to.
///   * Fix 2 (multi-NIC broadcast): teachers commonly have several active
///     IPv4 NICs (Wi-Fi + Ethernet + Hyper-V vSwitch + VPN tap).  The OS
///     picks one default-route NIC for IPAddress.Broadcast, so students on
///     the other NICs never receive the beacon.  We now enumerate every
///     "Up + non-loopback + IPv4" NIC and emit a per-NIC beacon to that
///     subnet's directed broadcast, plus the global 255.255.255.255 fallback.
///     TeacherIp inside each beacon is rewritten to the source IP of the
///     packet so the receiving student gets an IP that's reachable from
///     their NIC.
///   * Fix 5 (UDP socket options): ReuseAddress + ExclusiveAddressUse=false
///     on every UdpClient we open so leftover instances from a previous
///     crash, AV agents, or a dev host running side-by-side don't fail us
///     with SocketException on bind.
/// </summary>
public class TeacherBeaconService : IDisposable
{
    private const int BeaconPort = 7778;
    private const int IntervalMs = 2000;

    private readonly ILogger<TeacherBeaconService>? _logger;
    private readonly UdpClient _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // Phase 10.13 — value-based gate for MaybeLogLocalIp (replaces the prior
    // 30s time-based throttle).  We now log only when the primary IP actually
    // changes, so the steady-state log is silent and a DHCP lease / NIC swap
    // / VPN attach produces exactly one info line documenting the transition.
    private string? _lastLoggedPrimaryIp;

    public string ChannelId { get; set; } = "1234";
    public string ClassName { get; set; } = "";
    public int TcpPort { get; set; } = 7777;

    public TeacherBeaconService(ILogger<TeacherBeaconService>? logger = null)
    {
        _logger = logger;

        // Fix 5 — non-exclusive bind.  Use the no-arg ctor and tweak the underlying
        // socket before any bind happens.  The fallback global 255.255.255.255 send
        // path uses this UdpClient (without an explicit local bind, the OS picks
        // the default-route NIC, which is the historical behaviour and still useful).
        _udp = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.ExclusiveAddressUse = false;
        }
        catch (Exception ex) { _logger?.LogDebug(ex, "Beacon: setting UDP socket options failed"); }
        _udp.EnableBroadcast = true;

        // Fix 3 — read ChannelId from the unified registry (HKLM with HKCU fallback).
        ChannelId = ChannelIdRegistry.Read();
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
        _logger?.LogInformation("TeacherBeacon started (channel={Channel})", ChannelId);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _loop = null;
    }

    private async Task Loop(CancellationToken ct)
    {
        var globalEndpoint = new IPEndPoint(IPAddress.Broadcast, BeaconPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Fix 2 — emit one beacon per active NIC, each carrying that NIC's
                // own IPv4 as TeacherIp (so the receiving student gets a routable
                // address from their side), plus a global 255.255.255.255 fallback.
                var nicTargets = EnumerateBroadcastTargets().ToList();

                if (nicTargets.Count == 0)
                {
                    // Fix 1 — no usable NICs at all; fall back to the historical
                    // single-broadcast path so we don't drop the iteration silently.
                    var localIp = GetLocalIp();
                    if (localIp == null)
                    {
                        _logger?.LogWarning(
                            "Beacon: no usable local IPv4 found this tick; skipping broadcast.");
                    }
                    else
                    {
                        var payload = BuildPayload(localIp);
                        var bytes = MessagePack.MessagePackSerializer.Serialize(payload);
                        await _udp.SendAsync(bytes, bytes.Length, globalEndpoint).ConfigureAwait(false);
                        MaybeLogLocalIp(localIp, fanout: 1);
                    }
                }
                else
                {
                    foreach (var (src, subnetBcast) in nicTargets)
                    {
                        // Bind a transient socket to this NIC's source IP so the
                        // outbound packet leaves through the right interface.  The
                        // OS will reuse the same MTU/route table entry for every
                        // packet on this socket while it lives.
                        try
                        {
                            using var perNicUdp = new UdpClient(new IPEndPoint(src, 0));
                            try
                            {
                                perNicUdp.Client.SetSocketOption(SocketOptionLevel.Socket,
                                    SocketOptionName.ReuseAddress, true);
                                perNicUdp.ExclusiveAddressUse = false;
                            }
                            catch { /* best-effort */ }
                            perNicUdp.EnableBroadcast = true;

                            var payload = BuildPayload(src.ToString());
                            var bytes = MessagePack.MessagePackSerializer.Serialize(payload);

                            // Directed broadcast to the NIC's own subnet.
                            await perNicUdp.SendAsync(bytes, bytes.Length,
                                new IPEndPoint(subnetBcast, BeaconPort)).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug(ex,
                                "Beacon: per-NIC send failed (src={Src} bcast={Bcast})",
                                src, subnetBcast);
                        }
                    }

                    // Belt-and-suspenders: also emit a global 255.255.255.255 packet
                    // through the default-route socket in case the per-NIC subnet
                    // mask was odd (PPP/VPN tunnel without a real netmask, etc.).
                    try
                    {
                        var fallbackIp = nicTargets[0].Source.ToString();
                        var fallbackPayload = BuildPayload(fallbackIp);
                        var fallbackBytes = MessagePack.MessagePackSerializer.Serialize(fallbackPayload);
                        await _udp.SendAsync(fallbackBytes, fallbackBytes.Length, globalEndpoint).ConfigureAwait(false);
                    }
                    catch (Exception ex) { _logger?.LogDebug(ex, "Beacon: global broadcast send failed"); }

                    MaybeLogLocalIp(nicTargets[0].Source.ToString(), fanout: nicTargets.Count);
                }
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "Beacon iteration failed"); }

            try { await Task.Delay(IntervalMs, ct).ConfigureAwait(false); } catch { break; }
        }
    }

    private BeaconPayload BuildPayload(string teacherIp) => new()
    {
        ChannelId = ChannelId,
        TeacherIp = teacherIp,
        ClassName = ClassName,
        TcpPort = TcpPort,
        TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };

    // Phase 10.13 — replaced time-based rate limit (LocalIpLogIntervalSec) with
    // value-based: log only when the chosen primary IP changes between iterations.
    // During steady-state operation the log is silent; when DHCP lease, NIC swap,
    // or VPN attach changes the selection, we get exactly one info line documenting it.
    private void MaybeLogLocalIp(string ip, int fanout)
    {
        if (ip == _lastLoggedPrimaryIp) return;
        var transition = _lastLoggedPrimaryIp == null
            ? "initial"
            : $"{_lastLoggedPrimaryIp} -> {ip}";
        _lastLoggedPrimaryIp = ip;
        _logger?.LogInformation(
            "Beacon broadcasting (channel={Channel} primaryIp={Ip} fanout={Fanout} transition={Transition})",
            ChannelId, ip, fanout, transition);
    }

    public void Dispose()
    {
        Stop();
        try { _udp.Dispose(); } catch { }
    }

    /// <summary>
    /// Fix 3 — kept for backward compatibility.  New callers should use
    /// <see cref="ChannelIdRegistry.Read"/> / <see cref="ChannelIdRegistry.Write"/>
    /// directly.  This wrapper exists so any external script or third-party
    /// integration that still references the old static methods keeps working.
    /// </summary>
    [Obsolete("Use ChannelIdRegistry.Read() directly.")]
    public static string ReadChannelId() => ChannelIdRegistry.Read();

    /// <summary>Fix 3 — see <see cref="ReadChannelId"/>.</summary>
    [Obsolete("Use ChannelIdRegistry.Write() directly.")]
    public static void WriteChannelId(string id) => ChannelIdRegistry.Write(id);

    /// <summary>
    /// Fix 1 — return a usable local IPv4, or null when no fallback works.
    /// Order:  probe 8.8.8.8  →  probe 1.1.1.1  →  enumerate NICs with a
    /// default gateway and pick the first IPv4.  Returns null only when the
    /// machine has zero usable IPv4 NICs at all (offline laptop, VPN-only).
    /// </summary>
    internal static string? GetLocalIp()
    {
        // Probe 1: outbound to 8.8.8.8.  Cheapest if it works.
        var probed = TryProbeOutbound("8.8.8.8");
        if (probed != null) return probed;

        // Probe 2: outbound to 1.1.1.1.  Some firewalls block 8.8.8.8 but allow CF.
        probed = TryProbeOutbound("1.1.1.1");
        if (probed != null) return probed;

        // Fallback: enumerate adapters that look "real" — Up, IPv4-capable,
        // not loopback, and (preferred) carrying at least one default gateway.
        var enumerated = EnumerateLocalIPv4(preferGateway: true);
        if (!string.IsNullOrEmpty(enumerated)) return enumerated;

        // Last-ditch: any Up non-loopback IPv4 even without a gateway.
        enumerated = EnumerateLocalIPv4(preferGateway: false);
        return string.IsNullOrEmpty(enumerated) ? null : enumerated;
    }

    private static string? TryProbeOutbound(string remote)
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect(remote, 65530);
            return ((IPEndPoint)udp.Client.LocalEndPoint!).Address.ToString();
        }
        catch { return null; }
    }

    private static string? EnumerateLocalIPv4(bool preferGateway)
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses
                    .Any(g => g.Address != null
                        && g.Address.AddressFamily == AddressFamily.InterNetwork
                        && !g.Address.Equals(IPAddress.Any));
                if (preferGateway && !hasGateway) continue;

                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ua.Address)) continue;
                    return ua.Address.ToString();
                }
            }
        }
        catch { /* fall through */ }
        return null;
    }

    /// <summary>
    /// Fix 2 — yield a (Source, SubnetBroadcast) pair for every active IPv4 NIC.
    /// SubnetBroadcast is computed as (ip | ~mask) so directed broadcast lands
    /// on every host in that NIC's subnet.  Loopback and non-IPv4 NICs are
    /// excluded.
    /// </summary>
    private static IEnumerable<(IPAddress Source, IPAddress SubnetBroadcast)> EnumerateBroadcastTargets()
    {
        NetworkInterface[] nics;
        try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
        catch { yield break; }

        foreach (var nic in nics)
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch { continue; }

            foreach (var ua in props.UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ua.Address)) continue;
                if (ua.IPv4Mask == null
                    || ua.IPv4Mask.AddressFamily != AddressFamily.InterNetwork
                    || ua.IPv4Mask.Equals(IPAddress.Any))
                {
                    continue;
                }

                IPAddress bcast;
                try { bcast = ComputeSubnetBroadcast(ua.Address, ua.IPv4Mask); }
                catch { continue; }
                yield return (ua.Address, bcast);
            }
        }
    }

    private static IPAddress ComputeSubnetBroadcast(IPAddress ip, IPAddress mask)
    {
        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (ipBytes.Length != 4 || maskBytes.Length != 4)
            throw new ArgumentException("Expected IPv4 addresses.");
        var bcast = new byte[4];
        for (int i = 0; i < 4; i++) bcast[i] = (byte)(ipBytes[i] | (~maskBytes[i] & 0xFF));
        return new IPAddress(bcast);
    }
}
