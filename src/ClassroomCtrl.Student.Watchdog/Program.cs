using System.Diagnostics;

// Watchdog process — Spec §3.2.3, §7.2
// Phase 10.3: Service no longer spawns Agent or Watchdog (the Session-0
// respawn loop was the source of the "Agent started PID=..." log spam).
// Agent is launched by the HKLM Run autorun in the user session.  Watchdog
// has no privileged operations and currently only logs missing-Agent state;
// to actually run alongside the Agent again it must be added to the HKLM
// Run autorun (planned installer change for v1.1).  Until then this exe is
// effectively dormant — kept around so the spec-mandated process exists.

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