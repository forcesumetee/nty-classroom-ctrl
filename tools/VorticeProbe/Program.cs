// Phase 11-B inc3 — DXGI/Direct3D11 API surface probe.
//
// inc2 used this probe to resolve MF APIs.  inc3 needs DXGI Desktop Duplication:
//   IDXGIFactory1 / IDXGIAdapter1 / IDXGIOutput / IDXGIOutput1,
//   IDXGIOutputDuplication (AcquireNextFrame, ReleaseFrame, GetFramePointerShape),
//   OutduplFrameInfo, OutduplPointerShapeInformation, the DXGI_ERROR_* HRESULTs,
//   ID3D11Device + Texture2DDescription (CpuAccessFlags.Read, Usage.Staging),
//   ID3D11DeviceContext (CopyResource, Map, Unmap), MappedSubresource (RowPitch).
//
// Run from the repo root:
//   dotnet run --project tools\VorticeProbe\VorticeProbe.csproj -c Release > probe-inc3.txt

using System;
using System.Linq;
using System.Reflection;

// Try to load the Vortice.DXGI / Vortice.Direct3D11 / Vortice.DirectX assemblies
// directly from the NuGet cache for the .NET 8 target.  We can't reference them
// at compile time (Shared.csproj doesn't have them yet) but we can load + reflect
// via reflection.  Adjust the nuget cache path if needed.
var nugetCache = Environment.ExpandEnvironmentVariables("%USERPROFILE%\\.nuget\\packages");
string[] tryAssemblies = {
    System.IO.Path.Combine(nugetCache, "vortice.directx", "3.6.2", "lib", "net8.0", "Vortice.DirectX.dll"),
    System.IO.Path.Combine(nugetCache, "vortice.dxgi", "3.6.2", "lib", "net8.0", "Vortice.DXGI.dll"),
    System.IO.Path.Combine(nugetCache, "vortice.direct3d11", "3.6.2", "lib", "net8.0", "Vortice.Direct3D11.dll"),
    System.IO.Path.Combine(nugetCache, "vortice.mathematics", "1.9.2", "lib", "net8.0", "Vortice.Mathematics.dll"),
};

Console.WriteLine($"Loading Vortice DX assemblies from {nugetCache}...");
var loaded = new System.Collections.Generic.List<Assembly>();
foreach (var path in tryAssemblies)
{
    if (System.IO.File.Exists(path))
    {
        try
        {
            var asm = Assembly.LoadFrom(path);
            loaded.Add(asm);
            Console.WriteLine($"  loaded: {asm.GetName().Name} v{asm.GetName().Version} from {path}");
        }
        catch (Exception ex) { Console.WriteLine($"  FAIL: {path}: {ex.Message}"); }
    }
    else
    {
        Console.WriteLine($"  missing: {path}");
    }
}

Section("All types in Vortice.DXGI namespace (interface/struct/enum filter)");
foreach (var asm in loaded)
{
    var types = asm.GetTypes()
        .Where(t => (t.Namespace ?? "").StartsWith("Vortice.DXGI"))
        .Where(t => t.Name.Contains("Output") || t.Name.Contains("Adapter") || t.Name.Contains("Factory")
                 || t.Name.Contains("Duplication") || t.Name.Contains("Outdupl") || t.Name.Contains("Pointer"))
        .OrderBy(t => t.Name);
    foreach (var t in types)
        Console.WriteLine($"  {t.FullName}  ({(t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsValueType ? "struct" : "class")})");
}

Section("IDXGIOutput1 — declared methods (looking for DuplicateOutput)");
foreach (var asm in loaded)
{
    var t = asm.GetType("Vortice.DXGI.IDXGIOutput1");
    if (t == null) continue;
    PrintTypeBrief(t);
    PrintMethods(t, declaredOnly: true);
}

Section("IDXGIOutputDuplication — declared methods (AcquireNextFrame, ReleaseFrame, GetFramePointerShape)");
foreach (var asm in loaded)
{
    var t = asm.GetType("Vortice.DXGI.IDXGIOutputDuplication");
    if (t == null) continue;
    PrintTypeBrief(t);
    PrintMethods(t, declaredOnly: true);
}

Section("OutduplFrameInfo struct fields");
foreach (var asm in loaded)
{
    var t = asm.GetType("Vortice.DXGI.OutduplFrameInfo");
    if (t == null) continue;
    PrintTypeBrief(t);
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"    field {FormatType(f.FieldType)} {f.Name}");
}

Section("OutduplPointerShapeInformation struct fields");
foreach (var asm in loaded)
{
    var t = asm.GetType("Vortice.DXGI.OutduplPointerShapeInformation")
          ?? asm.GetType("Vortice.DXGI.OutduplPointerShapeInfo");
    if (t == null) continue;
    PrintTypeBrief(t);
    foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
        Console.WriteLine($"    field {FormatType(f.FieldType)} {f.Name}");
}

Section("IDXGIFactory1 — declared (EnumAdapters1)");
foreach (var asm in loaded)
{
    var t = asm.GetType("Vortice.DXGI.IDXGIFactory1");
    if (t == null) continue;
    PrintTypeBrief(t);
    PrintMethods(t, declaredOnly: true);
}

Section("IDXGIAdapter1 — declared (EnumOutputs)");
foreach (var asm in loaded)
{
    var t = asm.GetType("Vortice.DXGI.IDXGIAdapter1");
    if (t == null) continue;
    PrintMethods(t, declaredOnly: true);
}

Section("IDXGIOutput — declared (need this for IDXGIOutput1's QI base)");
foreach (var asm in loaded)
{
    var t = asm.GetType("Vortice.DXGI.IDXGIOutput");
    if (t == null) continue;
    PrintMethods(t, declaredOnly: true);
}

Section("CreateDXGIFactory entrypoint (DXGI.CreateDXGIFactory1 or similar)");
foreach (var asm in loaded)
{
    foreach (var t in asm.GetTypes().Where(t => t.IsClass && t.IsSealed && t.IsAbstract))
    {
        var m = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name.Contains("CreateDXGI") || m.Name == "CreateDXGIFactory1" || m.Name == "CreateDXGIFactory2")
                .ToList();
        foreach (var mi in m) Console.WriteLine($"  {t.FullName}.{mi.Name}({string.Join(",", mi.GetParameters().Select(p => $"{FormatType(p.ParameterType)} {p.Name}"))}) -> {FormatType(mi.ReturnType)}");
    }
}

Section("Vortice.Direct3D11 — D3D11.CreateDevice static + Texture2DDescription + ID3D11Texture2D");
foreach (var asm in loaded)
{
    if (!asm.GetName().Name!.Contains("Direct3D11")) continue;
    foreach (var t in asm.GetTypes().Where(t => t.IsClass && t.IsSealed && t.IsAbstract && t.Name == "D3D11"))
    {
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.StartsWith("Create")))
            Console.WriteLine($"  {t.Name}.{m.Name}({string.Join(",", m.GetParameters().Select(p => $"{FormatType(p.ParameterType)} {p.Name}"))}) -> {FormatType(m.ReturnType)}");
    }
    var tex = asm.GetType("Vortice.Direct3D11.Texture2DDescription");
    if (tex != null)
    {
        Console.WriteLine($"  -- {tex.FullName} --");
        foreach (var p in tex.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine($"    prop {FormatType(p.PropertyType)} {p.Name}");
        foreach (var f in tex.GetFields(BindingFlags.Public | BindingFlags.Instance))
            Console.WriteLine($"    field {FormatType(f.FieldType)} {f.Name}");
    }
    var iTex = asm.GetType("Vortice.Direct3D11.ID3D11Texture2D");
    if (iTex != null) { Console.WriteLine($"  -- {iTex.FullName} (interface) --"); PrintMethods(iTex, declaredOnly: true); }
    var iDev = asm.GetType("Vortice.Direct3D11.ID3D11Device");
    if (iDev != null)
    {
        Console.WriteLine($"  -- {iDev.FullName} (declared methods) --");
        foreach (var m in iDev.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).Where(m => m.Name.Contains("Texture") || m.Name.Contains("Resource") || m.Name == "QueryInterface" || m.Name.Contains("Context")))
            Console.WriteLine($"    {FormatType(m.ReturnType)} {m.Name}({string.Join(",", m.GetParameters().Select(p => $"{FormatType(p.ParameterType)} {p.Name}"))})");
    }
    var iCtx = asm.GetType("Vortice.Direct3D11.ID3D11DeviceContext");
    if (iCtx != null)
    {
        Console.WriteLine($"  -- {iCtx.FullName} (filtered Copy/Map methods) --");
        foreach (var m in iCtx.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => !m.IsSpecialName).Where(m => m.Name == "CopyResource" || m.Name.StartsWith("Map") || m.Name == "Unmap"))
            Console.WriteLine($"    {FormatType(m.ReturnType)} {m.Name}({string.Join(",", m.GetParameters().Select(p => $"{FormatType(p.ParameterType)} {p.Name}"))})");
    }
}

Section("DXGI Result constants (DXGI_ERROR_WAIT_TIMEOUT etc)");
foreach (var asm in loaded)
{
    foreach (var t in asm.GetTypes().Where(t => t.IsClass && t.IsSealed && t.IsAbstract && (t.Name == "ResultCode" || t.Name == "Vortice" || t.Name == "DXGI" || t.Name.Contains("Result"))))
    {
        var hits = t.GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Where(f => f.FieldType.Name.Contains("Result") || f.FieldType == typeof(int) || f.FieldType == typeof(uint))
                    .Where(f => f.Name.Contains("Timeout") || f.Name.Contains("AccessLost") || f.Name.Contains("AccessDenied")
                             || f.Name.Contains("DeviceRemoved") || f.Name.Contains("Unsupported"))
                    .ToList();
        if (hits.Count == 0) continue;
        Console.WriteLine($"  -- {t.FullName} --");
        foreach (var f in hits) Console.WriteLine($"    {f.FieldType.Name} {f.Name} = {f.GetValue(null)}");
    }
}

Section("D3D11.CreateDevice overloads + MappedSubresource + IDXGIAdapter.EnumOutputs");
foreach (var asm in loaded.Where(a => a.GetName().Name == "Vortice.Direct3D11"))
{
    var d3d11 = asm.GetType("Vortice.Direct3D11.D3D11");
    if (d3d11 != null)
        foreach (var m in d3d11.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name.StartsWith("CreateDevice")))
            Console.WriteLine($"  D3D11.{m.Name}({string.Join(",", m.GetParameters().Select(p => $"{FormatType(p.ParameterType)} {p.Name}"))}) -> {FormatType(m.ReturnType)}");
    var ms = asm.GetType("Vortice.Direct3D11.MappedSubresource");
    if (ms != null) { Console.WriteLine($"  -- {ms.FullName} --");
        foreach (var f in ms.GetFields(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"    field {FormatType(f.FieldType)} {f.Name}");
        foreach (var p in ms.GetProperties(BindingFlags.Public | BindingFlags.Instance)) Console.WriteLine($"    prop {FormatType(p.PropertyType)} {p.Name}"); }
}
foreach (var asm in loaded.Where(a => a.GetName().Name == "Vortice.DXGI"))
{
    var iAdapter = asm.GetType("Vortice.DXGI.IDXGIAdapter");
    if (iAdapter != null)
        foreach (var m in iAdapter.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
            Console.WriteLine($"  IDXGIAdapter.{m.Name}({string.Join(",", m.GetParameters().Select(p => $"{FormatType(p.ParameterType)} {p.Name}"))}) -> {FormatType(m.ReturnType)}");
}

Console.WriteLine("\n=== probe complete ===");

// ─── helpers ───
static void Section(string name) => Console.WriteLine($"\n========== {name} ==========");
static void PrintTypeBrief(Type t)
{
    Console.WriteLine($"  full={t.FullName}  isInterface={t.IsInterface}  base={t.BaseType?.Name}");
    var ifs = t.GetInterfaces();
    if (ifs.Length > 0) Console.WriteLine($"  interfaces: {string.Join(", ", ifs.Take(8).Select(i => i.Name))}");
}
static void PrintMethods(Type t, bool declaredOnly)
{
    var flags = BindingFlags.Public | BindingFlags.Instance;
    if (declaredOnly) flags |= BindingFlags.DeclaredOnly;
    foreach (var m in t.GetMethods(flags).Where(m => !m.IsSpecialName).OrderBy(m => m.Name))
    {
        var ps = string.Join(", ", m.GetParameters().Select(p => $"{FormatType(p.ParameterType)}{(p.IsOut ? "&out" : p.ParameterType.IsByRef ? "&ref" : "")} {p.Name}"));
        Console.WriteLine($"  {FormatType(m.ReturnType)} {m.Name}({ps})");
    }
}
static string FormatType(Type t)
{
    if (t.IsByRef) return FormatType(t.GetElementType()!);
    if (t.IsGenericType) return $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(FormatType))}>";
    return t.Name;
}
