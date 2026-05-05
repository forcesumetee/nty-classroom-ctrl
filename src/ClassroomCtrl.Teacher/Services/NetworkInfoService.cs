using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 1.5: Resolve the teacher PC's usable IPv4 addresses for display on the
/// Teacher console banner. Customers asked for this so deployment teams can read
/// the IP off the screen instead of running ipconfig.
///
/// Filters out: loopback (127.x), link-local (169.254.x), tunnel adapters,
/// virtual NICs (Hyper-V, VMware, VirtualBox, WSL), and any interface that isn't
/// operationally Up. Results are sorted Ethernet-first, then Wi-Fi, then others.
/// </summary>
public static class NetworkInfoService
{
    /// <summary>Substrings (case-insensitive) used to detect virtual NICs by name/description.</summary>
    private static readonly string[] VirtualKeywords =
    {
        "virtualbox", "vmware", "vmnet", "hyper-v", "vethernet",
        "tap-windows", "tap adapter", "wsl", "loopback pseudo",
        "docker", "wireguard"
    };

    public sealed class NicAddress
    {
        public required string IPAddress { get; init; }
        public required string DisplayName { get; init; }   // localized-friendly: "Wi-Fi" / "Ethernet" / actual NIC name
        public required NetworkInterfaceType Type { get; init; }
    }

    public static List<NicAddress> GetUsableIPv4Addresses()
    {
        var results = new List<NicAddress>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Unknown) continue;

            var nameAndDesc = ($"{nic.Name} {nic.Description}").ToLowerInvariant();
            if (VirtualKeywords.Any(k => nameAndDesc.Contains(k))) continue;

            var props = nic.GetIPProperties();
            foreach (var addr in props.UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var ip = addr.Address.ToString();
                if (ip.StartsWith("127.")) continue;
                if (ip.StartsWith("169.254.")) continue;

                var displayName = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                    NetworkInterfaceType.Ethernet => "Ethernet",
                    NetworkInterfaceType.GigabitEthernet => "Ethernet",
                    NetworkInterfaceType.FastEthernetT => "Ethernet",
                    NetworkInterfaceType.FastEthernetFx => "Ethernet",
                    _ => nic.Name,
                };

                results.Add(new NicAddress
                {
                    IPAddress = ip,
                    DisplayName = displayName,
                    Type = nic.NetworkInterfaceType,
                });
            }
        }

        // Sort: Ethernet first, then Wi-Fi, then other.
        return results
            .OrderBy(r => r.Type switch
            {
                NetworkInterfaceType.Ethernet => 0,
                NetworkInterfaceType.GigabitEthernet => 0,
                NetworkInterfaceType.FastEthernetT => 0,
                NetworkInterfaceType.FastEthernetFx => 0,
                NetworkInterfaceType.Wireless80211 => 1,
                _ => 2,
            })
            .ToList();
    }

    /// <summary>The recommended IP to share with students — first entry of <see cref="GetUsableIPv4Addresses"/>.</summary>
    public static string GetPrimaryIPv4()
    {
        var list = GetUsableIPv4Addresses();
        return list.Count > 0 ? list[0].IPAddress : "0.0.0.0";
    }

    /// <summary>
    /// Format all usable addresses into a single banner-friendly string.
    /// Examples:
    ///   "192.168.1.112 (Wi-Fi)"
    ///   "192.168.0.5 (Ethernet) | 10.0.0.7 (Wi-Fi)"
    /// </summary>
    public static string GetFormattedIPInfo()
    {
        var list = GetUsableIPv4Addresses();
        if (list.Count == 0) return "(no network)";
        return string.Join(" | ", list.Select(r => $"{r.IPAddress} ({r.DisplayName})"));
    }
}
