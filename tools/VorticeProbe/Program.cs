// Phase 11-B inc2 part2 round 3 — extended Vortice 3.6.2 API surface probe.
//
// Round 2 used this probe to resolve sync-mode SetOutputType / ProcessOutput
// names.  Round 3 needs to resolve the async MFT pump surface: MFTEnumEx,
// IMFActivate, IMFMediaEventGenerator, MediaEventType (METransformNeedInput /
// METransformHaveOutput), MFT_FRIENDLY_NAME, MFT_CATEGORY_VIDEO_ENCODER,
// MFT_ENUM_FLAG_*, MFT_MESSAGE_* (CommandDrain, NotifyEndOfStream,
// SetD3DManager), and check whether ICodecAPI is bound.
//
// Run from the repo root:
//   dotnet run --project tools\VorticeProbe\VorticeProbe.csproj
// Output is plain-text and intended to be piped to a file the dev reviews.

using System;
using System.Linq;
using System.Reflection;
using Vortice.MediaFoundation;

// Round 2 probe was a one-shot, single-output dump.  Round 3 needs many
// distinct surfaces, so split into named sections so the output is greppable.

Section("MediaFactory STATIC methods matching MFTEnumEx / MFTGetInfo / MFCreate*");
foreach (var m in typeof(MediaFactory)
    .GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(m => m.Name.StartsWith("MFTEnum") || m.Name.StartsWith("MFTGet")
             || m.Name.StartsWith("MFCreateDXGI") || m.Name.StartsWith("MFCreateAttributes")
             || m.Name.StartsWith("MFTRegister") || m.Name == "MFTEnum")
    .OrderBy(m => m.Name))
{
    PrintMethod(m);
}

Section("MediaFactory STATIC methods — full list (filter for what we need)");
foreach (var m in typeof(MediaFactory)
    .GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(m => !m.IsSpecialName)
    .OrderBy(m => m.Name))
{
    PrintMethod(m);
}

Section("IMFActivate type");
PrintTypeBrief(typeof(IMFActivate));
PrintMethods(typeof(IMFActivate), declaredOnly: true);

Section("IMFTransform — declared methods (verify ProcessMessage / SetInputType / SetOutputType etc)");
PrintMethods(typeof(IMFTransform), declaredOnly: true);

Section("IMFTransform — inherited (look for IMFMediaEventGenerator surface)");
PrintMethods(typeof(IMFTransform), declaredOnly: false);

Section("IMFMediaEventGenerator type");
var evGen = TryGetType("Vortice.MediaFoundation.IMFMediaEventGenerator");
if (evGen != null) { PrintTypeBrief(evGen); PrintMethods(evGen, declaredOnly: true); }
else Console.WriteLine("  (type not found in Vortice.MediaFoundation namespace)");

Section("IMFMediaEvent type");
var evType = TryGetType("Vortice.MediaFoundation.IMFMediaEvent");
if (evType != null)
{
    PrintTypeBrief(evType);
    Console.WriteLine("  -- declared methods --");
    PrintMethods(evType, declaredOnly: true);
    Console.WriteLine("  -- declared properties --");
    foreach (var p in evType.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(p => p.Name))
        Console.WriteLine($"    prop {FormatType(p.PropertyType)} {p.Name}");
}
else Console.WriteLine("  (type not found in Vortice.MediaFoundation namespace)");

Section("MediaEventType enum (METransformNeedInput / METransformHaveOutput)");
var mediaEvT = TryGetType("Vortice.MediaFoundation.MediaEventType");
if (mediaEvT != null)
{
    foreach (var n in Enum.GetNames(mediaEvT).OrderBy(x => x))
        Console.WriteLine($"  {n} = 0x{(int)Enum.Parse(mediaEvT, n):X4}");
}
else Console.WriteLine("  (type not found — searching for any enum containing 'Transform' members)");

Section("TMessageType (MFT_MESSAGE_*) — search for CommandDrain, NotifyEndOfStream, SetD3DManager");
var tmsg = TryGetType("Vortice.MediaFoundation.TMessageType");
if (tmsg != null)
{
    foreach (var n in Enum.GetNames(tmsg).OrderBy(x => x))
        Console.WriteLine($"  {n} = 0x{(int)Enum.Parse(tmsg, n):X4}");
}
else Console.WriteLine("  (type not found)");

Section("Vortice.MediaFoundation namespace — types matching MFT*, CodecApi, Codec, Activate");
foreach (var t in typeof(MediaFactory).Assembly
    .GetTypes()
    .Where(t => t.Namespace == "Vortice.MediaFoundation")
    .Where(t => t.Name.Contains("MFT") || t.Name.Contains("CodecApi") || t.Name.Contains("Codec")
             || t.Name.Contains("Activate") || t.Name.Contains("Friendly") || t.Name.Contains("Category"))
    .OrderBy(t => t.Name))
{
    Console.WriteLine($"  {t.FullName}  ({(t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class")})");
}

Section("Static GUID containers — look for MFT_CATEGORY_VIDEO_ENCODER, MFT_FRIENDLY_NAME, MF_TRANSFORM_*");
// Vortice tends to expose Win32 constants as `MFTxxx` or via `xxxKeys` static classes
// with public static Guid fields.  Enumerate likely containers and grep.
string[] containers = {
    "Vortice.MediaFoundation.MediaFactory",
    "Vortice.MediaFoundation.TransformAttributeKeys",
    "Vortice.MediaFoundation.MediaTypeAttributeKeys",
    "Vortice.MediaFoundation.SampleAttributeKeys",
    "Vortice.MediaFoundation.CaptureDeviceAttributeKeys",
    "Vortice.MediaFoundation.MFTransformCategoryGuids",
    "Vortice.MediaFoundation.MFTransformCategory",
    "Vortice.MediaFoundation.MfTransformCategoryGuids",
};
foreach (var name in containers)
{
    var ct = TryGetType(name);
    if (ct == null) continue;
    var fields = ct.GetFields(BindingFlags.Public | BindingFlags.Static)
                   .Where(f => f.FieldType == typeof(Guid))
                   .Where(f => f.Name.Contains("MFT") || f.Name.Contains("Transform")
                            || f.Name.Contains("Friendly") || f.Name.Contains("Category")
                            || f.Name.Contains("D3DManager") || f.Name.Contains("Async")
                            || f.Name.Contains("LowLatency") || f.Name.Contains("Codec"))
                   .OrderBy(f => f.Name)
                   .ToList();
    if (fields.Count == 0) continue;
    Console.WriteLine($"\n  -- {ct.FullName} --");
    foreach (var f in fields)
        Console.WriteLine($"    {f.Name} = {f.GetValue(null)}");
}

Section("Search all Vortice.MediaFoundation static Guid fields whose name suggests round-3 relevance");
var asm = typeof(MediaFactory).Assembly;
foreach (var t in asm.GetTypes().Where(t => t.IsClass && t.IsSealed && t.IsAbstract)) // static classes
{
    var hits = t.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(Guid))
                .Where(f => f.Name.Contains("FriendlyName")
                         || f.Name.Contains("Category")
                         || f.Name.Contains("AsyncUnlock")
                         || f.Name.Contains("LowLatency")
                         || f.Name.Contains("D3DManager")
                         || f.Name.Contains("Hardware")
                         || f.Name.StartsWith("MFT_")
                         || f.Name.StartsWith("CODECAPI_"))
                .ToList();
    if (hits.Count == 0) continue;
    Console.WriteLine($"  -- {t.FullName} --");
    foreach (var f in hits) Console.WriteLine($"    {f.Name} = {f.GetValue(null)}");
}

Section("Look for ICodecAPI in Vortice (often NOT bound — Direct COM via Marshal.QI)");
var codecApi = asm.GetTypes().FirstOrDefault(t => t.Name == "ICodecAPI" || t.Name == "IMFCodecAPI");
Console.WriteLine(codecApi != null
    ? $"  FOUND: {codecApi.FullName}"
    : "  NOT FOUND in Vortice.MediaFoundation — we'll declare a minimal [ComImport] interface manually");

Section("MFTOutputStreamInfo / IMFActivate.ActivateObject lookup");
var osi = TryGetType("Vortice.MediaFoundation.MFTOutputStreamInfo")
       ?? TryGetType("Vortice.MediaFoundation.TOutputStreamInformation");
if (osi != null)
{
    Console.WriteLine($"  found: {osi.FullName}");
    foreach (var f in osi.GetFields(BindingFlags.Public | BindingFlags.Instance).OrderBy(f => f.Name))
        Console.WriteLine($"    field {f.FieldType.Name} {f.Name}");
}

Section("MFT_ENUM_FLAG values (look for an enum)");
var enumFlag = asm.GetTypes().FirstOrDefault(t => t.IsEnum && t.Name.Contains("EnumFlag"));
if (enumFlag != null)
{
    Console.WriteLine($"  found enum: {enumFlag.FullName}");
    foreach (var n in Enum.GetNames(enumFlag))
        Console.WriteLine($"    {n} = 0x{(int)Enum.Parse(enumFlag, n):X8}");
}
else Console.WriteLine("  (no EnumFlag enum found — Win32 flags will be passed as raw uint)");

Section("All Vortice.MediaFoundation enums — dump every enum, names+values");
foreach (var t in asm.GetTypes().Where(t => t.IsEnum && t.Namespace == "Vortice.MediaFoundation").OrderBy(t => t.Name))
{
    Console.WriteLine($"  -- {t.Name} ({Enum.GetUnderlyingType(t).Name}) --");
    foreach (var n in Enum.GetNames(t).Take(60))
        Console.WriteLine($"    {n} = {Convert.ChangeType(Enum.Parse(t, n), Enum.GetUnderlyingType(t))}");
}

Section("IMFAttributes — declared methods (GetString shape, Set overloads)");
PrintMethods(TryGetType("Vortice.MediaFoundation.IMFAttributes")!, declaredOnly: true);

Section("TransformCategoryGuids fields");
var tcg = TryGetType("Vortice.MediaFoundation.TransformCategoryGuids");
if (tcg != null)
{
    foreach (var f in tcg.GetFields(BindingFlags.Public | BindingFlags.Static)
                         .Where(f => f.FieldType == typeof(Guid))
                         .OrderBy(f => f.Name))
        Console.WriteLine($"  {f.Name} = {f.GetValue(null)}");
}

Section("CodecAPI GUIDs anywhere in Vortice assembly");
foreach (var t in asm.GetTypes().Where(t => t.IsClass && t.IsSealed && t.IsAbstract))
{
    var hits = t.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(Guid))
                .Where(f => f.Name.Contains("CodecApi") || f.Name.Contains("AvEnc") || f.Name.Contains("ForceKey"))
                .ToList();
    if (hits.Count == 0) continue;
    Console.WriteLine($"  -- {t.FullName} --");
    foreach (var f in hits) Console.WriteLine($"    {f.Name} = {f.GetValue(null)}");
}

Section("IMFDXGIDeviceManager surface (need ResetDevice signature)");
var dxgiMgr = TryGetType("Vortice.MediaFoundation.IMFDXGIDeviceManager");
if (dxgiMgr != null) { PrintTypeBrief(dxgiMgr); PrintMethods(dxgiMgr, declaredOnly: true); }
else Console.WriteLine("  (type not found)");

Section("Look for MFCreateDXGIDeviceManager overload taking resetToken out");
foreach (var m in typeof(MediaFactory).GetMethods(BindingFlags.Public | BindingFlags.Static)
    .Where(m => m.Name.Contains("DXGI")))
    PrintMethod(m);

Console.WriteLine("\n=== probe complete ===");

// ─── helpers ───
static void Section(string name) => Console.WriteLine($"\n========== {name} ==========");
static Type? TryGetType(string fullName)
    => typeof(MediaFactory).Assembly.GetType(fullName, throwOnError: false);

static void PrintTypeBrief(Type t)
{
    Console.WriteLine($"  full={t.FullName}  isInterface={t.IsInterface}  base={t.BaseType?.Name}");
    var ifs = t.GetInterfaces();
    if (ifs.Length > 0)
        Console.WriteLine($"  interfaces: {string.Join(", ", ifs.Select(i => i.Name))}");
}

static void PrintMethod(MethodInfo m)
{
    var ps = string.Join(", ", m.GetParameters().Select(p => $"{FormatType(p.ParameterType)}{(p.IsOut ? "&out" : p.ParameterType.IsByRef ? "&ref" : "")} {p.Name}"));
    Console.WriteLine($"  {FormatType(m.ReturnType)} {m.Name}({ps})");
}

static void PrintMethods(Type t, bool declaredOnly)
{
    var flags = BindingFlags.Public | BindingFlags.Instance;
    if (declaredOnly) flags |= BindingFlags.DeclaredOnly;
    foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
        PrintMethod(m);
}

static string FormatType(Type t)
{
    if (t.IsByRef) return FormatType(t.GetElementType()!);
    if (t.IsGenericType)
        return $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(FormatType))}>";
    return t.Name;
}
