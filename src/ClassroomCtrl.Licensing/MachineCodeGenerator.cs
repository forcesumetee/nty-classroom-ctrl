using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace ClassroomCtrl.Licensing;

/// <summary>
/// Generates a stable hardware-fingerprint Machine Code for license binding.
/// Per Spec §14.2:
///   - Stable across reboots, Windows updates
///   - Tolerant of single-component swap (RAM/HDD/GPU)
///   - Changes only on motherboard replacement
/// Components: Win32_BaseBoard.SerialNumber + Win32_Processor.ProcessorId + first non-virtual MAC
/// Format: SHA256(concat).Hex().Substring(0,16) → "XXXX-XXXX-XXXX-XXXX"
/// </summary>
public static class MachineCodeGenerator
{
    public static string Generate()
    {
        var motherboard = QueryWmi("Win32_BaseBoard", "SerialNumber") ?? "UNKNOWN_MB";
        var cpu = QueryWmi("Win32_Processor", "ProcessorId") ?? "UNKNOWN_CPU";
        var mac = GetFirstPhysicalMac() ?? "000000000000";

        var raw = $"{motherboard}|{cpu}|{mac}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(hash)[..16]; // already uppercase

        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..]}";
    }

    private static string? QueryWmi(string wmiClass, string property)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher($"SELECT {property} FROM {wmiClass}");
            foreach (var obj in searcher.Get())
            {
                var val = obj[property]?.ToString()?.Trim();
                if (!string.IsNullOrWhiteSpace(val) && val != "0" && val != "None")
                    return val;
            }
        }
        catch { /* WMI may be disabled in some VMs */ }
        return null;
    }

    private static string? GetFirstPhysicalMac()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .Where(ni => !ni.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                          && !ni.Description.Contains("VMware", StringComparison.OrdinalIgnoreCase)
                          && !ni.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
                .Select(ni => ni.GetPhysicalAddress().ToString())
                .FirstOrDefault(s => !string.IsNullOrEmpty(s));
        }
        catch { return null; }
    }
}
