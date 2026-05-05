using ClassroomCtrl.Licensing;

// Machine Code ของเครื่องตัวเอง
var mc = MachineCodeGenerator.Generate();
Console.WriteLine($"Your Machine Code: {mc}");
Console.WriteLine($"Your Expected Key: {LicenseValidator.ComputeExpectedKey(mc)}");

Console.WriteLine();
Console.WriteLine("=== Test Vectors ===");

// Test กับ machine code คงที่ — ใช้เทียบกับ Cloud Keygen
string[] testCodes = { "ABCD-1234-EFGH-5678", "0000-0000-0000-0000", "TEST-TEST-TEST-TEST" };
foreach (var code in testCodes)
{
    var key = LicenseValidator.ComputeExpectedKey(code);
    Console.WriteLine($"  {code}  →  {key}");
}