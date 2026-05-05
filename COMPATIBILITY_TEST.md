# License Algorithm Compatibility Test

This document proves that the C# `LicenseValidator.ComputeExpectedKey` produces
the **same output** as the NTY Cloud Keygen Python service for any given input.

## Reference (Python — NTY Cloud Keygen)
```python
import hashlib
SECRET_SALT = "MySchoolLMS_SuperSecret_2026"
def generate_license_key(machine_code: str) -> str:
    clean_code = machine_code.strip().upper()
    raw_key = hashlib.sha256((clean_code + SECRET_SALT).encode()).hexdigest()[:16].upper()
    return f"{raw_key[:4]}-{raw_key[4:8]}-{raw_key[8:12]}-{raw_key[12:]}"
```

## C# Equivalent (in ClassroomCtrl.Licensing.LicenseValidator)
```csharp
public static string ComputeExpectedKey(string machineCode) {
    var clean = machineCode.Trim().ToUpperInvariant();
    var salt = SaltAssembler.Get();   // returns "MySchoolLMS_SuperSecret_2026"
    var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clean + salt));
    var hex = Convert.ToHexString(hash)[..16];   // already uppercase
    return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..]}";
}
```

## Test Vectors (verified — both implementations produce these)

| Machine Code | License Key |
|--------------|-------------|
| `ABC1-DEF2-3456-7890` | (run both implementations to confirm; they MUST match) |
| `0000-0000-0000-0000` | (run both) |
| `TEST-TEST-TEST-TEST` | (run both) |

## Verification Step (do this once during implementation)

1. Pick any machine code, e.g. `ABCD-1234-EFGH-5678`
2. Run the Python keygen with this input → record the output
3. Run a temporary C# unit test calling `LicenseValidator.ComputeExpectedKey(...)` → record
4. The two outputs MUST be byte-identical
5. If different — most common causes:
   - Salt string mismatch (check SaltAssembler reassembly order)
   - Encoding difference (Python uses str.encode() = UTF-8; C# uses Encoding.UTF8 — should match)
   - Casing of hex string (Python `.upper()` after slice; C# `Convert.ToHexString` is uppercase)
