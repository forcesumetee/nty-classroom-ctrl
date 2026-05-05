using System.Diagnostics;

// Watchdog process — Spec §3.2.3, §7.2
// Simplified: just monitors that Agent process is alive.
// Heartbeat mechanism removed for now (was conflicting with Agent's IPC pipe).
// Service spawns/respawns Agent on its own (already verified working).

const string AgentExeName = "ClassroomCtrl.Student.Agent";

Console.WriteLine($"Watchdog starting — monitoring {AgentExeName}");

while (true)
{
    try
    {
        bool agentAlive = Process.GetProcessesByName(AgentExeName).Length > 0;
        if (!agentAlive)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Agent not running");
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Check error: {ex.Message}");
    }
    await Task.Delay(5000);
}