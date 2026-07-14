using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClassroomCtrl.Licensing;

/// <summary>
/// TT-13-C — offline, machine-bound activation. Symmetric, same shape as the prior-project (VertexIFP)
/// scheme: key = SHA-256(machineCode + Salt), first 16 hex, grouped XXXX-XXXX-XXXX-XXXX. An offline
/// keygen (tools/NtyKeygen) produces a key from a machine code; the app validates the entered key
/// against the LIVE machine code every launch.
///
/// 🔴 Machine-binding is BY CONSTRUCTION: a key minted for machine A cannot validate on machine B,
/// because B's machineCode hashes to a different key. The security therefore lives in re-validating
/// against <see cref="MachineCode.Get"/> at launch — NEVER trusting a stored/copied key as proof.
///
/// The Windows product's WMI+DPAPI scheme is Windows-only; this is the macOS equivalent
/// (IOPlatformUUID machine id; Keychain storage is a later, non-security-critical convenience).
///
/// NOT WIRED into either app for the customer demo — activation must not gate the product on stage.
/// </summary>
public static class LicenseCore
{
    /// <summary>Product secret. 🔴 Rotate before shipping and keep it out of any public artifact —
    /// it is the only thing separating a valid key from a guessable one. Held here for the dev keygen.</summary>
    public const string Salt = "NTY-ClassroomCtrl-macOS::v1";

    /// <summary>Deterministic activation key for a machine code. Reference vector asserted in
    /// NtyKeygen --selftest: ComputeKey("TEST-0000-0001") == "F7E2-AF07-82B5-3BF4".</summary>
    public static string ComputeKey(string machineCode)
    {
        var hex = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(machineCode) + Salt)));
        return Group(hex[..16], 4);
    }

    /// <summary>True iff <paramref name="key"/> is the correct key for <paramref name="machineCode"/>.
    /// Dash/space/case-insensitive; constant-time compare. Pass the LIVE machine code, not a stored one.</summary>
    public static bool Validate(string machineCode, string? key)
    {
        if (string.IsNullOrWhiteSpace(machineCode) || string.IsNullOrWhiteSpace(key)) return false;
        return FixedEquals(Core(ComputeKey(machineCode)), Core(key));
    }

    /// <summary>Uppercase + trim (machine codes are compared normalized).</summary>
    public static string Normalize(string s) => (s ?? "").Trim().ToUpperInvariant();

    /// <summary>Group a hex string into dash-separated blocks of <paramref name="size"/>.</summary>
    public static string Group(string hex, int size)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < hex.Length; i++)
        {
            if (i > 0 && i % size == 0) sb.Append('-');
            sb.Append(hex[i]);
        }
        return sb.ToString();
    }

    // Alphanumeric core (drops dashes/spaces) for a format-insensitive compare.
    private static string Core(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static bool FixedEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        int diff = 0;
        for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
