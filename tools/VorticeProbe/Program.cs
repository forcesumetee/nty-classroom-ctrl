using System.Reflection;
using Vortice.MediaFoundation;

var t = typeof(IMFTransform);
Console.WriteLine($"--- {t.FullName} constructors ---");
foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
{
    var ps = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
    Console.WriteLine($"  ctor({ps})");
}
Console.WriteLine($"\n  IsClass={t.IsClass}  Base={t.BaseType?.FullName}");

// Same for ComObject base
var co = t.BaseType;
while (co != null && co != typeof(object))
{
    Console.WriteLine($"\n--- {co.FullName} constructors ---");
    foreach (var c in co.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
    {
        var ps = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  ctor({ps}) [{(c.IsPublic ? "public" : "non-public")}]");
    }
    co = co.BaseType;
}

// Check the well-known SharpGen patterns
Console.WriteLine("\n=== SharpGen.Runtime.MarshallingHelpers static methods ===");
var mh = Type.GetType("SharpGen.Runtime.MarshallingHelpers, SharpGen.Runtime");
if (mh != null)
{
    foreach (var m in mh.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        var gen = m.IsGenericMethod ? "<" + string.Join(",", m.GetGenericArguments().Select(g => g.Name)) + ">" : "";
        Console.WriteLine($"  static {m.ReturnType.Name} {m.Name}{gen}({ps})");
    }
}

// And ProcessOutputStatus enum to confirm signature
Console.WriteLine("\n=== ProcessOutputStatus ===");
var pos = typeof(ProcessOutputStatus);
foreach (var n in Enum.GetNames(pos)) Console.WriteLine($"  {n} = {(int)Enum.Parse(pos, n)}");

// Result struct
Console.WriteLine("\n=== SharpGen.Runtime.Result members ===");
var r = Type.GetType("SharpGen.Runtime.Result, SharpGen.Runtime");
if (r != null)
{
    foreach (var p in r.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).OrderBy(x => x.Name))
        Console.WriteLine($"  prop {p.PropertyType.Name} {p.Name}");
    foreach (var m in r.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName).OrderBy(x => x.Name))
    {
        var ps = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
        Console.WriteLine($"  method {m.ReturnType.Name} {m.Name}({ps})");
    }
}
