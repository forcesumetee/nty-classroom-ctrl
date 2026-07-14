using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ClassroomCtrl.Licensing;

/// <summary>
/// TT-13-C — this Mac's stable machine code, derived from IOPlatformUUID (the hardware id on
/// IOPlatformExpertDevice — the macOS analog of the Windows WMI machine id). The raw UUID is hashed
/// so the code the user reads/types is short and doesn't leak the hardware UUID:
/// SHA-256(IOPlatformUUID), first 12 hex, grouped XXXX-XXXX-XXXX.
///
/// Read via <c>ioreg</c> (which reads IOPlatformExpertDevice). A direct IOKit P/Invoke
/// (IOServiceGetMatchingService + IORegistryEntryCreateCFProperty) is the zero-dependency production
/// alternative; ioreg is used here for robustness (no CoreFoundation marshaling) and because this is
/// not on the app's hot path. Returns "" off macOS or if the id can't be read.
/// </summary>
public static class MachineCode
{
    public static string Get()
    {
        var raw = RawPlatformId();
        if (string.IsNullOrEmpty(raw)) return "";
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
        return LicenseCore.Group(hex[..12], 4);   // 12 hex → XXXX-XXXX-XXXX
    }

    /// <summary>The raw IOPlatformUUID (for diagnostics); "" if unavailable.</summary>
    public static string RawPlatformId()
    {
        if (!OperatingSystem.IsMacOS()) return "";
        try
        {
            var psi = new ProcessStartInfo("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            var m = Regex.Match(outp, "\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }
}
