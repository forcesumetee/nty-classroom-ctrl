using ClassroomCtrl.Licensing;

// NtyKeygen — offline activation-key generator (TT-13-C). No network; a machine code in, a key out.
//
//   NtyKeygen --this          print THIS Mac's machine code + its key (the demo fallback key)
//   NtyKeygen <machine-code>   print the key for a given machine code (what support runs for a customer)
//   NtyKeygen --selftest       the committed gate (reference vector + validate + machine-binding)

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("NtyKeygen — offline activation key generator");
    Console.WriteLine("  NtyKeygen --this            machine code + key for THIS Mac");
    Console.WriteLine("  NtyKeygen <MACHINE-CODE>    key for a given machine code");
    Console.WriteLine("  NtyKeygen --selftest        run the gate");
    return 0;
}

switch (args[0])
{
    case "--selftest":
        return SelfTest();

    case "--this":
    {
        var raw = MachineCode.RawPlatformId();
        var code = MachineCode.Get();
        if (string.IsNullOrEmpty(code))
        {
            Console.Error.WriteLine("Could not read IOPlatformUUID (not macOS, or ioreg unavailable).");
            return 1;
        }
        Console.WriteLine($"IOPlatformUUID : {raw}");
        Console.WriteLine($"Machine code   : {code}");
        Console.WriteLine($"Activation key : {LicenseCore.ComputeKey(code)}");
        return 0;
    }

    default:
    {
        var code = args[0];
        Console.WriteLine($"Machine code   : {LicenseCore.Normalize(code)}");
        Console.WriteLine($"Activation key : {LicenseCore.ComputeKey(code)}");
        return 0;
    }
}

static int SelfTest()
{
    int fail = 0;
    void Check(string label, bool ok)
    {
        Console.WriteLine((ok ? "  ✅ " : "  ❌ ") + label);
        if (!ok) fail++;
    }

    Console.WriteLine("=== NtyKeygen --selftest ===");

    // (1) Reference vector — computed independently (shasum) and asserted against the EXACT string,
    //     the VertexIFP discipline. If the algorithm or salt changes, this breaks loudly.
    const string refCode = "TEST-0000-0001";
    const string refKey = "F7E2-AF07-82B5-3BF4";
    var got = LicenseCore.ComputeKey(refCode);
    Check($"ComputeKey(\"{refCode}\") == \"{refKey}\" (got \"{got}\")", got == refKey);

    // (2) Validate: correct key passes; wrong/blank/garbage fails.
    Check("Validate accepts the correct key", LicenseCore.Validate(refCode, refKey));
    Check("Validate accepts it dash/space/case-insensitively", LicenseCore.Validate(refCode, " f7e2af0782b53bf4 "));
    Check("Validate rejects a wrong key", !LicenseCore.Validate(refCode, "0000-0000-0000-0000"));
    Check("Validate rejects an empty key", !LicenseCore.Validate(refCode, ""));
    Check("Validate rejects a truncated key", !LicenseCore.Validate(refCode, "F7E2-AF07"));

    // (3) 🔴 Machine-binding — a key minted for machine A must FAIL on machine B.
    const string codeA = "AAAA-1111-2222";
    const string codeB = "BBBB-3333-4444";
    var keyA = LicenseCore.ComputeKey(codeA);
    Check("different machines get different keys", LicenseCore.ComputeKey(codeA) != LicenseCore.ComputeKey(codeB));
    Check("A's key validates on A", LicenseCore.Validate(codeA, keyA));
    Check("🔴 A's key FAILS on B (machine-bound — a copied key doesn't work)", !LicenseCore.Validate(codeB, keyA));

    // (4) Determinism — same input, same key.
    Check("ComputeKey is deterministic", LicenseCore.ComputeKey(codeA) == keyA);

    Console.WriteLine(fail == 0
        ? "\n=== NTYKEYGEN SELFTEST PASS ✅ — reference vector + validate + machine-binding ==="
        : $"\n=== NTYKEYGEN SELFTEST FAIL ❌ ({fail} check(s)) ===");
    return fail == 0 ? 0 : 1;
}
