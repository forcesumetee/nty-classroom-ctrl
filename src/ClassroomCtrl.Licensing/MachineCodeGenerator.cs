using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;

namespace ClassroomCtrl.Licensing;

/// <summary>
/// Generates a stable hardware-fingerprint Machine Code for license binding.
/// Per Spec §14.2:
///   - Stable across reboots and Windows updates
///   - Stable across NIC enumeration order changes (Phase 10.14 fix)
///   - Tolerant of single-component swap (RAM/HDD/GPU)
///   - Changes only on motherboard replacement
///
/// Phase 10.14 license stability fix:
///   v1 (legacy, pre-10.14): hash(motherboard + CPU + first-up-physical-MAC)
///                            Unstable when LAN cable plug/unplug or Wi-Fi switches
///                            because Where(OperationalStatus.Up) + FirstOrDefault
///                            depended on which NIC happened to be live at call time.
///   v2 (current):          hash(motherboard + CPU + ALL-physical-MACs-sorted)
///                            Stable because (a) no status filter (we want what's
///                            physically present, not what's currently online), and
///                            (b) sort makes order deterministic regardless of how
///                            Windows enumerates the NICs.
///
/// Backward-compat: <see cref="GenerateLegacyVariants"/> returns every possible v1
/// machine code for the current hardware so <see cref="LicenseStore"/> can validate
/// licenses issued under the v1 algorithm without forcing every existing customer
/// to re-activate.
/// </summary>
public static class MachineCodeGenerator
{
    /// <summary>
    /// Current (v2) machine code. Hash includes ALL physical MACs sorted, so the
    /// result doesn't depend on which NIC happens to be "first" in OS enumeration
    /// order or which one is currently Up.
    /// </summary>
    public static string Generate()
    {
        var motherboard = QueryWmi("Win32_BaseBoard", "SerialNumber") ?? "UNKNOWN_MB";
        var cpu = QueryWmi("Win32_Processor", "ProcessorId") ?? "UNKNOWN_CPU";
        var allMacs = GetAllPhysicalMacs();

        // Phase 10.14 — sort ordinal so the result is deterministic regardless of
        // OS enumeration order. Join with '|' so "AB" + "CD" can't collide with
        // a single MAC "ABCD".  When no MACs are present (rare — pure VM with
        // virtual NICs filtered out, or WMI disabled), fall back to the same
        // zero-string the v1 code used so customers running on that profile
        // get a stable code from both algorithms.
        var sortedMacs = allMacs.Count > 0
            ? string.Join("|", allMacs.OrderBy(m => m, System.StringComparer.Ordinal))
            : "000000000000";

        var raw = $"{motherboard}|{cpu}|{sortedMacs}";
        return HashToCode(raw);
    }

    /// <summary>
    /// Phase 10.14 — every possible v1 machine code for this hardware.
    ///
    /// The v1 algorithm was hash(motherboard + CPU + first-up-physical-MAC), where
    /// "first" was whatever <see cref="NetworkInterface.GetAllNetworkInterfaces"/>
    /// returned first AND was OperationalStatus.Up at activation time.  We don't
    /// know which MAC was "first" during the customer's original activation, so we
    /// enumerate one variant per MAC currently visible on the machine, plus the
    /// all-zero fallback that the v1 code used when no NIC was Up.
    ///
    /// Call this from <see cref="LicenseStore.IsCurrentMachineActivated"/> when the
    /// v2 code doesn't match the stored value — typical for customers who activated
    /// before 10.14 shipped.  Returns codes in no particular order; caller does
    /// equality comparison.
    /// </summary>
    public static IEnumerable<string> GenerateLegacyVariants()
    {
        var motherboard = QueryWmi("Win32_BaseBoard", "SerialNumber") ?? "UNKNOWN_MB";
        var cpu = QueryWmi("Win32_Processor", "ProcessorId") ?? "UNKNOWN_CPU";

        foreach (var mac in GetAllPhysicalMacs())
        {
            yield return HashToCode($"{motherboard}|{cpu}|{mac}");
        }
        // v1 fell back to "000000000000" when no NIC was Up at activation time.
        yield return HashToCode($"{motherboard}|{cpu}|000000000000");
    }

    private static string HashToCode(string raw)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var hex = Convert.ToHexString(hash)[..16]; // uppercase
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

    /// <summary>
    /// All physical NICs (Ethernet + Wi-Fi) currently visible to Windows, excluding
    /// virtual adapters (VPN, VMware, Hyper-V, Loopback).
    ///
    /// Phase 10.14 — removed the OperationalStatus.Up filter that v1 had. We want
    /// every MAC that physically exists on the machine, regardless of whether it's
    /// currently linked.  A laptop with both LAN and Wi-Fi must always see both
    /// MACs whether the cable is plugged in or not, otherwise the machine code
    /// flips between v2 states too.
    /// </summary>
    private static List<string> GetAllPhysicalMacs()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                                                       or NetworkInterfaceType.Wireless80211)
                .Where(ni => !ni.Description.Contains("Virtual", System.StringComparison.OrdinalIgnoreCase)
                          && !ni.Description.Contains("VMware", System.StringComparison.OrdinalIgnoreCase)
                          && !ni.Description.Contains("Hyper-V", System.StringComparison.OrdinalIgnoreCase)
                          && !ni.Description.Contains("VPN", System.StringComparison.OrdinalIgnoreCase)
                          && !ni.Description.Contains("Loopback", System.StringComparison.OrdinalIgnoreCase))
                .Select(ni => ni.GetPhysicalAddress().ToString())
                .Where(s => !string.IsNullOrEmpty(s) && s != "000000000000")
                .Distinct()
                .OrderBy(s => s, System.StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }
}
