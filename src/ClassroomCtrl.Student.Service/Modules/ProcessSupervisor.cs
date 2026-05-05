using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace ClassroomCtrl.Student.Service.Modules;

/// <summary>
/// Spawns and supervises the user-session Agent and Watchdog processes
/// (Spec §3.2). This skeleton uses Process.Start for clarity; production
/// must use CreateProcessAsUser to launch into the active interactive
/// session token (since the Service runs as LocalSystem in session 0).
///
/// References:
///   - WTSGetActiveConsoleSessionId
///   - WTSQueryUserToken
///   - CreateProcessAsUser
/// </summary>
public class ProcessSupervisor
{
    private readonly ILogger<ProcessSupervisor> _logger;
    private Process? _agent;
    private Process? _watchdog;

    public ProcessSupervisor(ILogger<ProcessSupervisor> logger)
    {
        _logger = logger;
    }

    public Task SpawnAgentAndWatchdogAsync(CancellationToken ct)
    {
        _ = Task.Run(SuperviseLoop, ct);
        return Task.CompletedTask;
    }

    private async Task SuperviseLoop()
    {
        while (true)
        {
            try
            {
                if (_agent is null || _agent.HasExited)
                {
                    _agent = StartInUserSession("ClassroomCtrl.Student.Agent");
                    _logger.LogInformation("Agent started PID={Pid}", _agent?.Id);
                }
                if (_watchdog is null || _watchdog.HasExited)
                {
                    _watchdog = StartInUserSession("ClassroomCtrl.Student.Watchdog");
                    _logger.LogInformation("Watchdog started PID={Pid}", _watchdog?.Id);
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Supervisor error"); }
            await Task.Delay(5000);
        }
    }

    private Process? StartInUserSession(string exeBaseName)
    {
        // Production layout: all .exe files in the same install folder as the Service.
        var serviceDir = Path.GetDirectoryName(Environment.ProcessPath)!;
        var prodPath = Path.Combine(serviceDir, exeBaseName + ".exe");

        // Dev fallback: sibling project's bin folder.
        // serviceDir = C:\ClassroomCtrl\src\ClassroomCtrl.Student.Service\bin\Debug\net10.0-windows\win-x64
        // target    = C:\ClassroomCtrl\src\<exeBaseName>\bin\Debug\net10.0-windows\win-x64\<exeBaseName>.exe
        var configRel = Path.Combine("bin", "Debug", "net10.0-windows", "win-x64", exeBaseName + ".exe");
        var srcDir = Directory.GetParent(serviceDir)?.Parent?.Parent?.Parent?.Parent?.FullName;
        var devPath = srcDir != null ? Path.Combine(srcDir, exeBaseName, configRel) : null;

        string? path = File.Exists(prodPath) ? prodPath
                     : (devPath != null && File.Exists(devPath)) ? devPath
                     : null;

        if (path == null)
        {
            _logger.LogWarning("Could not locate {Exe}. Checked:\n  prod: {Prod}\n  dev:  {Dev}",
                exeBaseName, prodPath, devPath ?? "(no dev path)");
            return null;
        }

        try
        {
            // TODO production: use CreateProcessAsUser with the active session token.
            // Dev: launch normally — process inherits the Service's session.
            var psi = new ProcessStartInfo(path) { UseShellExecute = false };
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start {Path}", path);
            return null;
        }
    }
}