using ClassroomCtrl.Avalonia.Sandbox.ViewModels;
using ClassroomCtrl.Avalonia.Sandbox.Views;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// TT-8-D — owns the single teacher-screen viewer window. Open() on ScreenStreamStart, Close() on
/// ScreenStreamStop (reliable — bug #5) or disconnect. Mirrors the Teacher's ScreenViewController.
/// Called from ConnectionViewModel's events, which fire on the UI thread (WireClient events are
/// Post-marshaled), so no extra marshalling is needed.
/// </summary>
public sealed class TeacherScreenController
{
    private readonly TeacherScreenViewModel _vm;
    private TeacherScreenWindow? _window;

    public TeacherScreenController(TeacherScreenViewModel vm) => _vm = vm;

    public void Open()
    {
        if (_window is not null) { _window.Activate(); return; }
        _window = new TeacherScreenWindow { DataContext = _vm };
        _window.Closed += (_, _) => _window = null;
        _window.Show();
    }

    public void Close()
    {
        var w = _window;
        _window = null;
        w?.Close();
    }
}
