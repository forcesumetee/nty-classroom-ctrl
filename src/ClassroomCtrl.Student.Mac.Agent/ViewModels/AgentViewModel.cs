using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassroomCtrl.Student.Mac.Agent.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Student.Mac.Agent.ViewModels;

/// <summary>
/// Backing state for the tray icon + its NativeMenu (bound in App.axaml). Holds the observable
/// projection of three inputs — daemon-IPC connectivity, lock state, and (future) teacher name —
/// and recomputes the status line + glyph whenever any changes. Every mutator is expected to be
/// called on the UI thread (the App marshals IPC events via Dispatcher before touching this).
/// </summary>
public sealed partial class AgentViewModel : ObservableObject
{
    private readonly Action _openDownloads;
    private readonly Action _openChat;
    private readonly Action _quit;

    // Cached drawn glyphs (no emoji-font dependency, deterministic) — created once, post-Avalonia-init.
    private readonly WindowIcon _iconConnected = MakeDot(Color.FromRgb(0x2E, 0xCC, 0x71)); // green
    private readonly WindowIcon _iconDisconnected = MakeDot(Color.FromRgb(0xE7, 0x4C, 0x3C)); // red
    private readonly WindowIcon _iconLocked = MakeLock(Color.FromRgb(0xF1, 0xC4, 0x0F));    // gold

    // Raw inputs.
    private bool _teacherConnected;
    private bool _locked;

    [ObservableProperty] private string _statusText = "Status: Disconnected";
    [ObservableProperty] private string _teacherText = "Teacher: —";
    [ObservableProperty] private bool _hasTeacher;
    [ObservableProperty] private string _toolTip = "NTY ClassroomCtrl";
    [ObservableProperty] private WindowIcon? _trayIcon;

    public AgentViewModel(Action openDownloads, Action openChat, Action quit)
    {
        _openDownloads = openDownloads;
        _openChat = openChat;
        _quit = quit;
        Refresh();
    }

    // ── Inputs from the IPC layer (call on the UI thread) ──

    /// <summary>
    /// The Agent↔daemon IPC link changed. Losing it means we can no longer know the teacher state, so a
    /// drop is treated as "teacher disconnected" (and clears the teacher name). A (re)connect leaves the
    /// state as-is until the daemon pushes its TeacherStatusNotify snapshot.
    /// </summary>
    public void SetIpcConnected(bool connected)
    {
        if (!connected) SetTeacherStatus(false, null);
    }

    /// <summary>Teacher-link snapshot pushed by the daemon (TeacherStatusNotify).</summary>
    public void SetTeacherStatus(bool connected, string? teacherName)
    {
        _teacherConnected = connected;
        SetTeacher(connected ? teacherName : null);
        Refresh();
    }

    public void SetLocked(bool locked)
    {
        if (_locked == locked) return;
        _locked = locked;
        Refresh();
    }

    public void SetTeacher(string? teacher)
    {
        HasTeacher = !string.IsNullOrWhiteSpace(teacher);
        TeacherText = AgentStatus.TeacherLine(teacher);
    }

    private void Refresh()
    {
        var (kind, status, tip) = AgentStatus.Describe(_teacherConnected, _locked);
        StatusText = status;
        ToolTip = tip;
        TrayIcon = kind switch
        {
            AgentStatus.Kind.Connected => _iconConnected,
            AgentStatus.Kind.Locked => _iconLocked,
            _ => _iconDisconnected,
        };
    }

    // ── Menu commands ──

    [RelayCommand] private void OpenDownloads() => _openDownloads();
    [RelayCommand] private void OpenChat() => _openChat();
    [RelayCommand] private void Quit() => _quit();

    // ── Drawn glyphs ──

    private static WindowIcon MakeDot(Color c)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
            ctx.DrawEllipse(new SolidColorBrush(c), null, new Point(18, 18), 8.5, 8.5);
        return new WindowIcon(rtb);
    }

    private static WindowIcon MakeLock(Color c)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var brush = new SolidColorBrush(c);
            var shackle = new StreamGeometry();
            using (var g = shackle.Open())
            {
                g.BeginFigure(new Point(13.5, 17), false);
                g.ArcTo(new Point(22.5, 17), new Size(4.5, 4.5), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(brush, 3), shackle);
            ctx.DrawRectangle(brush, null, new RoundedRect(new Rect(11, 16, 14, 12), 2.5));
        }
        return new WindowIcon(rtb);
    }
}
