using Microsoft.Win32;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClassroomCtrl.Licensing;

/// <summary>
/// Reads/writes activated license to HKLM\Software\NTY\ClassroomCtrl\License.
/// Encrypted with DPAPI machine scope (Spec §14.4).
/// Writing requires admin privileges — done by installer or activation flow elevated.
/// </summary>
public static class LicenseStore
{
    private const string RegPath = @"Software\NTY\ClassroomCtrl\License";
    private const string ValueName = "Data";

    public record StoredLicense(string Key, string MachineCode, DateTime ActivatedAtUtc, string Edition);

    public static StoredLicense? Load()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegPath);
            var enc = key?.GetValue(ValueName) as byte[];
            if (enc == null || enc.Length == 0) return null;
            var json = ProtectedData.Unprotect(enc, null, DataProtectionScope.LocalMachine);
            return JsonSerializer.Deserialize<StoredLicense>(json);
        }
        catch { return null; }
    }

    public static void Save(StoredLicense lic)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(lic);
        var enc = ProtectedData.Protect(json, null, DataProtectionScope.LocalMachine);
        using var key = Registry.LocalMachine.CreateSubKey(RegPath, writable: true);
        key.SetValue(ValueName, enc, RegistryValueKind.Binary);
    }

    public static void Clear()
    {
        try { Registry.LocalMachine.DeleteSubKeyTree(RegPath, throwOnMissingSubKey: false); }
        catch { /* ignore */ }
    }

    /// <summary>
    /// Full check: license exists AND matches current machine AND key matches algorithm.
    /// Per Spec §14.5 re-activation triggers.
    /// </summary>
    public static bool IsCurrentMachineActivated()
    {
        var lic = Load();
        if (lic == null) return false;
        var currentMc = MachineCodeGenerator.Generate();
        if (!string.Equals(lic.MachineCode, currentMc, StringComparison.OrdinalIgnoreCase))
            return false;
        return LicenseValidator.Validate(currentMc, lic.Key);
    }
}
