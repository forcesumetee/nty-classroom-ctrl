using H.NotifyIcon;
using System;
using System.Windows;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 10.14 (Item 9) — minimal tray notifier for Teacher.
///
/// Teacher is a foreground app; the tray icon's only job here is to provide a
/// Windows-toast surface for incoming chat from students when the Teacher window
/// is hidden, minimized, or unfocused.  Deliberately no context menu and no
/// "Open Window" handler — those would imply tray-resident operation, which
/// Teacher doesn't do (the spec keeps Teacher as a foreground app).
///
/// Mirrors the relevant slice of <c>Student.Agent.Tray.TrayIconManager</c>:
/// pack:// URI load of <c>classroom_icon.ico</c>, deep-copy of the System.Drawing.Icon
/// handle so the source stream can be disposed immediately.
/// </summary>
public class TrayNotifier : IDisposable
{
    private TaskbarIcon? _icon;

    public void Initialize()
    {
        _icon = new TaskbarIcon { ToolTipText = "Classroom Control" };

        try
        {
            var uri = new Uri("pack://application:,,,/Assets/classroom_icon.ico", UriKind.Absolute);
            var sri = Application.GetResourceStream(uri);
            if (sri != null)
            {
                using var s = sri.Stream;
                _icon.Icon = new System.Drawing.Icon(s);
            }
        }
        catch
        {
            // Non-fatal: tray icon falls back to default empty box if the
            // resource is missing under an unusual build configuration.
        }

        _icon.ForceCreate();
    }

    public void ShowBalloon(string title, string message)
    {
        try { _icon?.ShowNotification(title, message); }
        catch { /* balloon is non-critical; never let it crash chat receive */ }
    }

    public void Dispose() => _icon?.Dispose();
}
