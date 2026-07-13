namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// Phase 32-C — pure mapping from (connection status, lock state) → the menubar tray's glyph
/// kind + hover tooltip + menu status-line text. Deliberately Avalonia-free so the state logic is
/// unit-testable headlessly (MockTeacher --traytest); the icon RENDERING and menu interaction are
/// proven visually (LIVE). Locked takes precedence over connection state — a locked student shows
/// the lock glyph whether or not the wire is up, because the enforced lock is the salient status.
/// </summary>
public static class TrayPresenter
{
    public enum Kind { Connected, Connecting, Disconnected, Locked }

    public static (Kind kind, string tooltip, string statusLine) Describe(
        WireStatus status, bool locked, string teacherIp)
    {
        if (locked)
            return (Kind.Locked, "NTY ClassroomCtrl — 🔒 Locked by teacher", "🔒 Locked by teacher");

        return status switch
        {
            WireStatus.Connected =>
                (Kind.Connected, $"NTY ClassroomCtrl — Connected to {teacherIp}", $"Connected to {teacherIp}"),
            WireStatus.Connecting or WireStatus.Reconnecting =>
                (Kind.Connecting, "NTY ClassroomCtrl — Connecting…", "Connecting…"),
            _ =>
                (Kind.Disconnected, "NTY ClassroomCtrl — Disconnected", "Disconnected"),
        };
    }
}
