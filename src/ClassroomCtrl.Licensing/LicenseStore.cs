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
    ///
    /// Phase 10.14 — extended with a v1 (legacy) backward-compat path so customers
    /// who activated before the algorithm change don't get forced into a re-activation
    /// after upgrading.  The v2 path is the fast path (and the only path that fresh
    /// 10.14+ activations will hit); v1 variants are tried only if v2 doesn't match
    /// the stored MC.
    /// </summary>
    public static bool IsCurrentMachineActivated()
    {
        var lic = Load();
        if (lic == null) return false;

        // Phase 10.14 — fast path: try v2 (current) first.  Most installs that
        // activate AFTER this fix ships will have stored a v2 code, so this path
        // hits and the legacy loop never runs.
        var currentMc = MachineCodeGenerator.Generate();
        if (string.Equals(lic.MachineCode, currentMc, StringComparison.OrdinalIgnoreCase))
            return LicenseValidator.Validate(currentMc, lic.Key);

        // Phase 10.14 — backward-compat: the license was issued for a v1 machine
        // code.  Enumerate every legacy variant the current hardware could produce
        // and look for a match.  If any matches the stored MC, the license is
        // valid for this machine even though the algorithm changed — without
        // this loop, every existing customer would have to re-activate after the
        // 10.14 upgrade, which is exactly the cliff we're avoiding.
        //
        // Pass the matched legacy MC into Validate so the key check uses the same
        // code the key was issued for (Validate computes ExpectedKey from the MC
        // argument; passing the v2 MC would compute a different expected key).
        foreach (var legacy in MachineCodeGenerator.GenerateLegacyVariants())
        {
            if (string.Equals(lic.MachineCode, legacy, StringComparison.OrdinalIgnoreCase))
                return LicenseValidator.Validate(legacy, lic.Key);
        }

        return false;
    }
}
