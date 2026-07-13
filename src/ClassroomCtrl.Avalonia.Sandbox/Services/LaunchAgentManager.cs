using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// Phase 32-E — install/remove a per-user LaunchAgent so the student app auto-starts at login
/// (the macOS equivalent of the Windows student's HKLM\Run entry). Locked decisions:
///   • per-user <c>~/Library/LaunchAgents/</c> — NO sudo (self-install); the all-users
///     <c>/Library/LaunchAgents/</c> path is for admin/MDM lab deployment (needs admin);
///   • <c>RunAtLoad=true</c> + <c>KeepAlive=false</c> — starts at login, but a Cmd+Q stays quit
///     (faithful to the Windows Agent, and it preserves the M22 quit-to-escape dead-man path).
///
/// EXECUTABLE PATH: the plist self-targets <see cref="Environment.ProcessPath"/> — it points at
/// whatever binary installed it. To avoid ever registering a throwaway dev path (a `dotnet run`
/// apphost), <see cref="Enable"/> is GATED on <see cref="IsBundled"/> (running from a .app). The
/// real install therefore only happens from the packaged app (32-F/32-G); this class's logic and
/// the --launchagenttest harness are proven headlessly against a TEMP dir + a fake launchctl, so
/// automated testing installs ZERO real agents and leaves no trace.
///
/// launchctl: uses the modern domain-targeted <c>bootstrap gui/$UID</c> / <c>bootout</c> (macOS
/// 11+), falling back to the legacy <c>load</c>/<c>unload</c> if bootstrap is unavailable.
/// </summary>
public sealed partial class LaunchAgentManager
{
    public const string Label = "com.nty.classroomctrl.student";
    private const string FileName = Label + ".plist";

    private readonly string _agentsDir;
    private readonly string _programPath;
    private readonly Func<string, string[], int> _runctl;

    [LibraryImport("libc")] private static partial uint getuid();

    public LaunchAgentManager(string? agentsDir = null, string? programPath = null,
                              Func<string, string[], int>? runctl = null)
    {
        _agentsDir = agentsDir ?? DefaultAgentsDir;
        _programPath = programPath ?? Environment.ProcessPath ?? "";
        _runctl = runctl ?? RealLaunchctl;
    }

    /// <summary><c>~/Library/LaunchAgents</c> (per-user, no sudo).</summary>
    public static string DefaultAgentsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public string PlistPath => Path.Combine(_agentsDir, FileName);

    /// <summary>True only when the running binary lives inside a .app bundle. Enable is gated on
    /// this so a dev/dotnet path is never registered as an auto-start.</summary>
    public bool IsBundled => _programPath.Contains("/Contents/MacOS/", StringComparison.Ordinal);

    /// <summary>Enabled = the RunAtLoad plist is present (so it starts at the next login).</summary>
    public bool IsEnabled => File.Exists(PlistPath);

    /// <summary>The launchd plist XML: RunAtLoad=true, KeepAlive=false, ProgramArguments = the
    /// currently-running executable (spaces are fine — each arg is its own &lt;string&gt;).</summary>
    public string BuildPlistXml()
    {
        var exe = SecurityElement.Escape(_programPath);
        return
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n" +
            "<dict>\n" +
            $"    <key>Label</key>\n    <string>{Label}</string>\n" +
            $"    <key>ProgramArguments</key>\n    <array>\n        <string>{exe}</string>\n    </array>\n" +
            "    <key>RunAtLoad</key>\n    <true/>\n" +
            "    <key>KeepAlive</key>\n    <false/>\n" +
            "    <key>ProcessType</key>\n    <string>Interactive</string>\n" +
            "</dict>\n</plist>\n";
    }

    /// <summary>Write + bootstrap the LaunchAgent. Refuses (no-op) unless running from a .app bundle.</summary>
    public (bool ok, string message) Enable()
    {
        if (!IsBundled)
            return (false, "Auto-start is available only from the packaged app (not `dotnet run`).");
        try
        {
            Directory.CreateDirectory(_agentsDir);
            File.WriteAllText(PlistPath, BuildPlistXml());
            var domain = $"gui/{getuid()}";
            _runctl("bootout", new[] { domain, PlistPath });          // idempotent (ignore "not loaded")
            if (_runctl("bootstrap", new[] { domain, PlistPath }) != 0)
                _runctl("load", new[] { PlistPath });                 // legacy fallback
            return (true, "Start at login: ON");
        }
        catch (Exception ex) { return (false, $"enable failed: {ex.Message}"); }
    }

    /// <summary>Unload + delete the plist — a complete removal that leaves no trace. Safe when idle.
    /// This is also the uninstall (toggle-off == uninstall).</summary>
    public (bool ok, string message) Disable()
    {
        try
        {
            var domain = $"gui/{getuid()}";
            _runctl("bootout", new[] { domain, PlistPath });          // best-effort
            _runctl("unload", new[] { PlistPath });                   // legacy best-effort
            if (File.Exists(PlistPath)) File.Delete(PlistPath);
            return (true, "Start at login: OFF");
        }
        catch (Exception ex) { return (false, $"disable failed: {ex.Message}"); }
    }

    private static int RealLaunchctl(string subcommand, string[] args)
    {
        var psi = new ProcessStartInfo("/bin/launchctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add(subcommand);
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            return p.WaitForExit(5000) ? p.ExitCode : -1;
        }
        catch { return -1; }
    }
}
