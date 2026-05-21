using System.Diagnostics;
using Serilog;

// Phase 10.13 — Watchdog rewritten from a dormant log-only stub into a real
// respawn loop for Service.exe.  Scope per the Option C decision: monitor
// Service only (not Agent — Agent crashes are rarer, and HKLM Run handles
// its autostart at logon).
//
// History: Phase 10.3 gutted the prior Watchdog when Service moved out of
// Session 0; the file kept the spec-mandated process name but did nothing.
// Now that Service runs in the user session via HKLM Run (Phase 10.11),
// Watchdog provides the "respawn on crash" guarantee that HKLM Run by
// itself does not.
//
// Output type is WinExe (see .csproj) so no console window appears when the
// installer launches us at logon.  All diagnostics go to Serilog file sink:
// %PROGRAMDATA%\NTY\ClassroomCtrl\logs\watchdog-.log (same path style as
// Service per Phase 10.8 convention).

const string ServiceExeName = "ClassroomCtrl.Student.Service";
const string ServiceExeFile = "ClassroomCtrl.Student.Service.exe";
const int CheckIntervalMs = 5000;
const int CrashWindowSec = 60;
const int CrashThreshold = 5;
const int BackoffPauseMs = 5 * 60 * 1000; // 5 min

var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                           "NTY", "ClassroomCtrl", "logs", "watchdog-.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    // Phase 10.14 (Item 7) — explicit UTF-8, same reason as Service.Program.cs.
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
        encoding: System.Text.Encoding.UTF8)
    .CreateLogger();

// Watchdog respawns Service.exe from its own install directory.  AppContext.BaseDirectory
// resolves to the directory Inno Setup deployed both exes into, so we don't need a
// registry lookup or hard-coded path.
var installDir = AppContext.BaseDirectory;
var serviceExePath = Path.Combine(installDir, ServiceExeFile);

Log.Information("Watchdog starting — PID={Pid} installDir={Dir} target={Target}",
    Environment.ProcessId, installDir, serviceExePath);

// Rolling window of crash timestamps; if more than CrashThreshold crashes happen
// within CrashWindowSec, we pause for BackoffPauseMs to avoid hammering a Service
// that is failing for a structural reason (missing dependency, corrupted config).
var recentCrashes = new Queue<DateTime>();

try
{
    while (true)
    {
        try
        {
            bool alive = Process.GetProcessesByName(ServiceExeName).Length > 0;

            if (!alive)
            {
                // Trim window before counting.
                var cutoff = DateTime.UtcNow.AddSeconds(-CrashWindowSec);
                while (recentCrashes.Count > 0 && recentCrashes.Peek() < cutoff)
                    recentCrashes.Dequeue();

                if (recentCrashes.Count >= CrashThreshold)
                {
                    Log.Warning("Crash-loop detected ({Count} crashes in {Window}s) — pausing {PauseMin}min",
                        recentCrashes.Count, CrashWindowSec, BackoffPauseMs / 60000);
                    await Task.Delay(BackoffPauseMs);
                    recentCrashes.Clear();
                    continue;
                }

                if (!File.Exists(serviceExePath))
                {
                    Log.Error("Service exe not found at {Path} — cannot respawn", serviceExePath);
                }
                else
                {
                    try
                    {
                        var psi = new ProcessStartInfo(serviceExePath)
                        {
                            WorkingDirectory = installDir,
                            UseShellExecute = false,
                            CreateNoWindow = true,
                        };
                        var p = Process.Start(psi);
                        recentCrashes.Enqueue(DateTime.UtcNow);
                        Log.Information("Respawned Service (PID={Pid})", p?.Id ?? -1);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to respawn Service from {Path}", serviceExePath);
                        recentCrashes.Enqueue(DateTime.UtcNow);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Watchdog check iteration failed");
        }

        await Task.Delay(CheckIntervalMs);
    }
}
finally
{
    Log.CloseAndFlush();
}
