namespace ClassroomCtrl.Student.Mac.Agent.Services;

/// <summary>
/// Pure mapping from (teacher connected, locked) → the tray's glyph kind + status-line + tooltip.
/// Deliberately Avalonia-free so the state logic is unit-testable headlessly (mirrors the Sandbox's
/// TrayPresenter). "Connected" means the daemon reports a live TCP link to the Teacher (pushed over IPC
/// via TeacherStatusNotify); if the Agent loses the daemon IPC link it can't know the teacher state, so
/// it treats that as disconnected too. Locked takes precedence: an enforced lock is the salient status.
/// </summary>
public static class AgentStatus
{
    public enum Kind { Connected, Disconnected, Locked }

    public static (Kind kind, string statusText, string toolTip) Describe(bool teacherConnected, bool locked)
    {
        if (locked)
            return (Kind.Locked, "Status: Locked by teacher", "NTY ClassroomCtrl — Locked by teacher");

        return teacherConnected
            ? (Kind.Connected, "Status: Connected", "NTY ClassroomCtrl — Connected")
            : (Kind.Disconnected, "Status: Disconnected", "NTY ClassroomCtrl — Disconnected");
    }

    /// <summary>Menu line for the teacher; em-dash placeholder when the daemon hasn't reported one.</summary>
    public static string TeacherLine(string? teacher)
        => string.IsNullOrWhiteSpace(teacher) ? "Teacher: —" : $"Teacher: {teacher}";
}
