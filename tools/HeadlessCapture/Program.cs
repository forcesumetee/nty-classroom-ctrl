// HeadlessCapture — reusable off-screen Avalonia screenshot tool (headless + Skia).
// Promoted from the Phase 24.3–25.1 scratchpad harness (Phase 25.2).
//
//   Modes:
//     --list                         Enumerate registered scenarios (name + metadata).
//     --scenario <name>              Capture a registered scenario (Scenarios.cs).
//     --view-type <FQN> [--vm-type <FQN>]
//                                    Ad-hoc: reflectively build a Control (+ optional
//                                    parameterless VM as DataContext) and capture it.
//   Common:
//     --output <path>   (default capture.png)   --width N   --height N
//     --theme light|dark
//
// Examples:
//   dotnet run --project tools/HeadlessCapture -- --list
//   dotnet run --project tools/HeadlessCapture -- --scenario theme --output docs/phase-25.0-B-theme.png
//   dotnet run --project tools/HeadlessCapture -- --view-type ClassroomCtrl.Avalonia.Sandbox.Views.ThemeShowcaseView --output x.png
using System.Linq;
using System.Reflection;
using Avalonia.Controls;
using HeadlessCapture;

var a = ParseArgs(args);

if (a.ContainsKey("list") || args.Length == 0)
{
    Console.WriteLine("HeadlessCapture — registered scenarios:\n");
    foreach (var (name, s) in Scenarios.All.OrderBy(kv => kv.Key))
    {
        Console.WriteLine($"  {name,-12} [Phase {s.OriginalPhase}]  {s.Width}x{s.Height}");
        Console.WriteLine($"  {"",-12} {s.Description}");
        if (s.OriginalScreenshot != null)
            Console.WriteLine($"  {"",-12} baseline: {s.OriginalScreenshot}");
        Console.WriteLine();
    }
    Console.WriteLine("Ad-hoc:  --view-type <FQN> [--vm-type <FQN>] --output x.png [--width N --height N --theme light|dark]");
    return 0;
}

string output = a.GetValueOrDefault("output") ?? "capture.png";
string? theme = a.GetValueOrDefault("theme");

CaptureRunner.EnsureInit();
CaptureRunner.SetTheme(theme);

// ── scenario mode ──
if (a.TryGetValue("scenario", out var scenarioName))
{
    if (!Scenarios.All.TryGetValue(scenarioName!, out var sc))
    {
        Console.Error.WriteLine($"Unknown scenario '{scenarioName}'. Run --list to see options.");
        return 2;
    }
    int w = IntArg(a, "width", sc.Width);
    int h = IntArg(a, "height", sc.Height);
    var window = sc.BuildWindow();
    window.Width = w; window.Height = h;
    CaptureRunner.CaptureToFile(window, output, sc.AfterShow);
    Console.WriteLine($"saved {output}  (scenario '{scenarioName}', {w}x{h})");
    return 0;
}

// ── generic reflection mode ──
if (a.TryGetValue("view-type", out var viewType))
{
    int w = IntArg(a, "width", 800);
    int h = IntArg(a, "height", 600);
    var control = CreateByName<Control>(viewType!)
                  ?? throw new InvalidOperationException($"Could not create Control '{viewType}'.");
    if (a.TryGetValue("vm-type", out var vmType))
        control.DataContext = CreateByName<object>(vmType!)
                              ?? throw new InvalidOperationException($"Could not create VM '{vmType}'.");
    var window = CaptureRunner.AsWindow(control, w, h);
    CaptureRunner.CaptureToFile(window, output, afterShow: null);
    Console.WriteLine($"saved {output}  (view-type '{viewType}', {w}x{h})");
    return 0;
}

Console.Error.WriteLine("Nothing to do. Use --list, --scenario <name>, or --view-type <FQN>.");
return 2;


// ── helpers ──
static Dictionary<string, string?> ParseArgs(string[] args)
{
    var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--")) continue;
        var key = args[i][2..];
        string? val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : null;
        d[key] = val;
    }
    return d;
}

static int IntArg(Dictionary<string, string?> a, string key, int fallback)
    => a.TryGetValue(key, out var v) && int.TryParse(v, out var n) ? n : fallback;

// Resolve a type by full name across loaded assemblies (the Sandbox is referenced,
// so its assembly is loaded) and construct via its parameterless constructor.
static T? CreateByName<T>(string fullName) where T : class
{
    var type = AppDomain.CurrentDomain.GetAssemblies()
        .Select(asm => asm.GetType(fullName, throwOnError: false))
        .FirstOrDefault(t => t != null);
    if (type == null)
    {
        Console.Error.WriteLine($"Type '{fullName}' not found in loaded assemblies. " +
                                "Ensure it lives in a referenced project and has a parameterless ctor.");
        return null;
    }
    return Activator.CreateInstance(type) as T;
}
