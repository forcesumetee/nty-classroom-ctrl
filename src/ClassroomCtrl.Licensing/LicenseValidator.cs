using System.Security.Cryptography;
using System.Text;

namespace ClassroomCtrl.Licensing;

/// <summary>
/// Validates a user-entered license key against the expected key for a given machine code.
/// Algorithm MUST match the NTY Cloud Keygen exactly (Spec §14.3).
///
/// Reference (Python):
///   raw = sha256((machineCode.strip().upper() + SECRET_SALT).encode()).hexdigest()
///   key = raw[:16].upper()
///   "{key[:4]}-{key[4:8]}-{key[8:12]}-{key[12:]}"
/// </summary>
public static class LicenseValidator
{
    /// <summary>
    /// Compute the expected key for a machine code. The salt is split across
    /// multiple constants assembled at runtime per Spec §14.6 mitigation #2.
    /// In production, also apply ConfuserEx obfuscation.
    /// </summary>
    public static string ComputeExpectedKey(string machineCode)
    {
        var clean = machineCode.Trim().ToUpperInvariant();
        var salt = SaltAssembler.Get();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clean + salt));
        var hex = Convert.ToHexString(hash)[..16]; // uppercase
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..]}";
    }

    public static bool Validate(string machineCode, string userEnteredKey)
    {
        var expected = ComputeExpectedKey(machineCode);
        var entered = userEnteredKey.Trim().ToUpperInvariant();

        // Constant-time comparison to prevent timing-based extraction
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(entered);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

/// <summary>
/// Salt is split across multiple non-obvious constants and recombined at runtime.
/// This is mitigation #2 from Spec §14.6 — does not prevent extraction by a determined
/// attacker, but raises the bar. Must be paired with code obfuscation.
///
/// VALUE MUST EXACTLY MATCH the SECRET_SALT in NTY Cloud Keygen FastAPI service.
/// </summary>
internal static class SaltAssembler
{
    // SECRET_SALT = "MySchoolLMS_SuperSecret_2026"
    // Split into 4 fragments out-of-order; reassemble at runtime.
    private static readonly string _f3 = "Secret_";
    private static readonly string _f1 = "MySchool";
    private static readonly string _f4 = "2026";
    private static readonly string _f2 = "LMS_Super";

    public static string Get() => _f1 + _f2 + _f3 + _f4;
}
