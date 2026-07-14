using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassroomCtrl.Student.Mac;

/// <summary>
/// macOS power-state and application lifecycle commands.
///
/// Replaces the Windows <c>PowerCommands</c> (shutdown.exe / WTSLogoffSession / wtsapi32.dll)
/// with macOS equivalents:
///   • Shutdown / Reboot → native BSD <c>/sbin/shutdown -h now</c> / <c>-r now</c>
///   • Logoff            → <c>launchctl asuser &lt;uid&gt; osascript …'log out'</c> in the console session
///   • Launch / Quit app → <c>open -a</c> / <c>osascript -e 'tell application "…" to quit'</c>
///
/// <b>Elevation model.</b> <c>/sbin/shutdown</c> performs an immediate, non-interactive power
/// transition (no "are you sure?" dialog) — the correct behaviour for teacher-driven classroom
/// control, but it requires <b>root</b>. In production this daemon runs as a root launchd
/// <c>LaunchDaemon</c> (see <c>scripts/com.nty.classroom.daemon.plist</c>), so the call executes
/// directly. We deliberately do <b>not</b> prefix <c>sudo</c> — a root daemon doesn't need it, and
/// <c>sudo</c> would demand an interactive password (hanging a headless daemon) once un-elevated.
///
/// <b>Debug safety.</b> When the process is <b>not</b> elevated (e.g. <c>dotnet run</c> during
/// development), the power methods do NOT attempt the transition — they log the <i>intent</i> and
/// return. This keeps a dev session on the operator's own Mac from being rebooted out from under
/// them by a (mock) teacher command. The guard is the same one that gates real execution in
/// production, so the debug path and the deploy path share a single branch.
///
/// Every public method is fire-and-forget with full error capture (process spawn
/// failure, non-zero exit, timeout) so a single failing command never takes down
/// the daemon.
/// </summary>
public static partial class MacPowerCommands
{
    // ──────────── Power state ────────────

    /// <summary>Initiate an immediate system shutdown (<c>shutdown -h now</c>).
    /// No-op with an intent log when the process is not root (debug-safe).</summary>
    public static void Shutdown(ILogger logger) => RunPowerAction("SHUTDOWN", "-h", logger);

    /// <summary>Initiate an immediate system restart (<c>shutdown -r now</c>).
    /// No-op with an intent log when the process is not root (debug-safe).</summary>
    public static void Reboot(ILogger logger) => RunPowerAction("RESTART", "-r", logger);

    /// <summary>Log out the current interactive (console) user.
    /// No-op with an intent log when the process is not root (debug-safe).</summary>
    public static void Logoff(ILogger logger)
    {
        if (!Elevated(out uint euid))
        {
            logger.LogWarning(
                "[MacPower] LOG OUT requested but process is not elevated (euid={Euid}) — logging intent " +
                "only, no action taken. Executes for real under the root launchd daemon.", euid);
            return;
        }

        // A root daemon lives outside the user's Aqua session, so the AppleScript must be injected
        // INTO the console user's GUI session via `launchctl asuser <uid>` — a plain osascript from
        // the daemon context would have no session to act on.
        var uid = ConsoleUserId(logger);
        if (uid is null) { logger.LogWarning("[MacPower] LOG OUT skipped — no console user is logged in"); return; }

        logger.LogWarning("[MacPower] Executing LOG OUT of console user (uid {Uid})", uid);
        RunProcess("launchctl",
            ["asuser", uid, "/usr/bin/osascript", "-e", "tell application \"System Events\" to log out"],
            logger, "LOG OUT");
    }

    // ──────────── Application lifecycle ────────────

    /// <summary>
    /// Launch a macOS application by name (e.g. "Safari", "Google Chrome").
    /// Uses <c>open -a</c> which resolves the .app bundle from /Applications
    /// (or ~/Applications, or Spotlight index) and activates it.
    /// </summary>
    public static void LaunchApp(string appName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            logger.LogWarning("[MacPower] LaunchApp called with empty appName — ignored");
            return;
        }

        logger.LogInformation("[MacPower] Launching app: {App}", appName);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "open",
                Arguments = $"-a {EscapeArg(appName)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            if (proc is null) { logger.LogWarning("[MacPower] 'open -a' returned null process"); return; }
            if (!proc.WaitForExit(10_000))
            {
                logger.LogWarning("[MacPower] 'open -a {App}' timed out (10 s)", appName);
                try { proc.Kill(); } catch { }
                return;
            }
            if (proc.ExitCode != 0)
            {
                var stderr = proc.StandardError.ReadToEnd();
                logger.LogWarning("[MacPower] 'open -a {App}' exit={Code}: {Err}",
                    appName, proc.ExitCode, stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MacPower] Failed to launch app: {App}", appName);
        }
    }

    /// <summary>
    /// Ask an application to quit gracefully via AppleScript.
    /// This sends the standard <c>quit</c> Apple Event — the app may prompt
    /// to save unsaved work (same semantics as Cmd+Q).
    /// </summary>
    public static void QuitApp(string appName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(appName))
        {
            logger.LogWarning("[MacPower] QuitApp called with empty appName — ignored");
            return;
        }

        logger.LogInformation("[MacPower] Quitting app: {App}", appName);
        RunOsaScript($"tell application \"{EscapeQuotes(appName)}\" to quit", logger);
    }

    /// <summary>
    /// Force-kill a running application by process name.
    /// Falls back to <c>pkill -9</c> when a graceful quit is not desired.
    /// </summary>
    public static void ForceQuitApp(string processName, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            logger.LogWarning("[MacPower] ForceQuitApp called with empty processName — ignored");
            return;
        }

        logger.LogWarning("[MacPower] Force-killing process: {Name}", processName);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "pkill",
                Arguments = $"-9 {EscapeArg(processName)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            });
            proc?.WaitForExit(5_000);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MacPower] pkill failed for: {Name}", processName);
        }
    }

    // ──────────── Internal helpers ────────────

    /// <summary><c>geteuid(2)</c> — effective user id. 0 ⇒ root.</summary>
    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuid();

    /// <summary>True when running as root. <paramref name="euid"/> carries the value for logging.</summary>
    private static bool Elevated(out uint euid) { euid = GetEuid(); return euid == 0; }

    /// <summary>
    /// Shared shutdown/restart executor. Gated on root: when un-elevated we only log the intent
    /// (debug-safe); when elevated we exec <c>/sbin/shutdown &lt;flag&gt; now</c> — an immediate,
    /// non-interactive transition, no confirmation dialog.
    /// </summary>
    private static void RunPowerAction(string label, string flag, ILogger logger)
    {
        if (!Elevated(out uint euid))
        {
            logger.LogWarning(
                "[MacPower] {Label} requested but process is not elevated (euid={Euid}) — logging intent " +
                "only, no action taken. Executes for real under the root launchd daemon.", label, euid);
            return;
        }

        logger.LogWarning("[MacPower] Executing system {Label} (/sbin/shutdown {Flag} now)", label, flag);
        RunProcess("/sbin/shutdown", [flag, "now"], logger, label);
    }

    /// <summary>
    /// Resolve the uid of the current console (logged-in GUI) user via <c>stat -f%u /dev/console</c>,
    /// or null when no one is logged in (login window). Used to target <c>launchctl asuser</c>.
    /// </summary>
    private static string? ConsoleUserId(ILogger logger)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/stat",
                ArgumentList = { "-f", "%u", "/dev/console" },
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (proc is null) return null;
            var uid = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(3_000);
            // uid "0" means the login-window / no GUI user is active — treat as "no console user".
            return string.IsNullOrEmpty(uid) || uid == "0" ? null : uid;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[MacPower] Failed to resolve console user");
            return null;
        }
    }

    /// <summary>
    /// Spawn a process with per-argument (non-shell) escaping and full failure capture — spawn
    /// failure, non-zero exit, and a 15-second timeout. Fire-and-forget safe: never throws.
    /// </summary>
    private static void RunProcess(string fileName, string[] args, ILogger logger, string label)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) { logger.LogWarning("[MacPower] {Label}: process start returned null", label); return; }

            if (!proc.WaitForExit(15_000))
            {
                logger.LogWarning("[MacPower] {Label}: timed out (15 s) — killing", label);
                try { proc.Kill(); } catch { /* already gone */ }
                return;
            }
            if (proc.ExitCode != 0)
            {
                var stderr = proc.StandardError.ReadToEnd();
                logger.LogWarning("[MacPower] {Label}: exit={Code}: {Err}", label, proc.ExitCode, stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MacPower] {Label}: spawn failed", label);
        }
    }

    /// <summary>
    /// Run an AppleScript one-liner via <c>osascript -e '…'</c>.
    /// Handles process spawn failure, non-zero exit, and a 15-second timeout
    /// (covers the case where the System Events prompt hangs waiting for user
    /// input on a locked session — the daemon must not block indefinitely).
    /// </summary>
    private static void RunOsaScript(string script, ILogger logger)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                Arguments = $"-e '{script}'",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (proc is null)
            {
                logger.LogWarning("[MacPower] osascript process start returned null");
                return;
            }

            if (!proc.WaitForExit(15_000))
            {
                logger.LogWarning("[MacPower] osascript timed out (15 s) — killing");
                try { proc.Kill(); } catch { }
                return;
            }

            if (proc.ExitCode != 0)
            {
                var stderr = proc.StandardError.ReadToEnd();
                logger.LogWarning("[MacPower] osascript exit={Code}: {Err}",
                    proc.ExitCode, stderr.Trim());
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[MacPower] osascript spawn failed");
        }
    }

    /// <summary>Escape double-quotes inside an AppleScript string literal.</summary>
    private static string EscapeQuotes(string s) => s.Replace("\"", "\\\"");

    /// <summary>Shell-escape a single argument (wrap in double-quotes, escape inner quotes).</summary>
    private static string EscapeArg(string s) => $"\"{EscapeQuotes(s)}\"";
}
