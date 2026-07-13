using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;
using ClassroomCtrl.Avalonia.Teacher.Views;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-3-B — owns the open ScreenViewWindows (one per student) and enforces the
/// lifecycle discipline that keeps streams from leaking:
/// <list type="bullet">
///   <item><see cref="OpenOrFocus"/>: at most one window per student — a second
///   double-tap FOCUSES the existing window (no duplicate RequestStudentStreamAsync).</item>
///   <item>Each window's own <c>Closed</c> handler stops the stream, so EVERY close
///   path — user close, <see cref="CloseAll"/> on quit, <see cref="HandleStudentDisconnected"/>
///   — funnels through exactly one stop.</item>
///   <item><see cref="CloseAll"/>: app-quit closes every window (→ each stops its
///   stream). Call it BEFORE disposing the session so the stop can still be sent.</item>
///   <item><see cref="HandleStudentDisconnected"/>: a student leaving (roster
///   StudentRemoved, raised on a background thread) closes their window, marshaled to
///   the UI thread.</item>
/// </list>
/// The window factory is injectable so a headless test can drive the dedupe / close /
/// disconnect logic; production uses the default <c>new ScreenViewWindow(vm)</c>.
/// </summary>
public sealed class ScreenViewController
{
    private readonly IStudentStreamSource _source;
    private readonly Func<ScreenViewModel, Window> _windowFactory;
    private readonly Dictionary<Guid, Window> _open = new();

    public ScreenViewController(IStudentStreamSource source, Func<ScreenViewModel, Window>? windowFactory = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _windowFactory = windowFactory ?? (vm => new ScreenViewWindow(vm));
    }

    /// <summary>Open screen views currently on screen.</summary>
    public int OpenCount => _open.Count;

    /// <summary>Open a screen view for the student, or focus the existing one. UI thread.</summary>
    public void OpenOrFocus(Guid studentId, string displayName, string machineName)
    {
        if (_open.TryGetValue(studentId, out var existing))
        {
            existing.Activate();     // already viewing this student — bring it forward, don't re-request
            return;
        }
        var vm = new ScreenViewModel(_source, studentId, displayName, machineName);
        var win = _windowFactory(vm);
        _open[studentId] = win;
        win.Closed += (_, _) => _open.Remove(studentId);
        win.Show();
    }

    /// <summary>App quit / main-window close: close every screen view (each stops its
    /// stream). Call BEFORE disposing the session so the stop envelopes can be sent.</summary>
    public void CloseAll()
    {
        foreach (var win in _open.Values.ToArray())   // copy: Close() → Closed → _open.Remove during iteration
            win.Close();
        _open.Clear();
    }

    /// <summary>Roster StudentRemoved (a background thread): if this student's window is
    /// open, close it (→ stop stream). Marshaled to the UI thread.</summary>
    public void HandleStudentDisconnected(Guid studentId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_open.TryGetValue(studentId, out var win))
                win.Close();
        });
    }
}
