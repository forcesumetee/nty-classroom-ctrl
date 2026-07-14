using System;
using Avalonia.Controls;
using Avalonia.Input;
using ClassroomCtrl.Avalonia.Teacher.Services;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;

namespace ClassroomCtrl.Avalonia.Teacher;

public partial class MainWindow : Window
{
    /// <summary>TT-3-B — raised when a student tile is double-tapped (the "view this
    /// student's screen" affordance while the context menu is deferred). App wires this
    /// to the ScreenViewController; MainWindow stays unaware of window management.</summary>
    public event Action<StudentTileViewModel>? StudentActivated;

    /// <summary>TT-5-B — raised when a per-student command is chosen from a tile's context
    /// menu. App wires this to the StudentCommandController; MainWindow stays unaware of
    /// the send path (same separation as StudentActivated).</summary>
    public event Action<(StudentTileViewModel Vm, StudentCommand Command)>? StudentCommandRequested;

    public MainWindow() => InitializeComponent();

    private void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // The handler is attached to the StudentCard element in the DataTemplate, so
        // sender is the card and its DataContext is the tile VM.
        if (sender is Control { DataContext: StudentTileViewModel vm })
            StudentActivated?.Invoke(vm);
    }

    private void Card_CommandRequested(object? sender, StudentCommandEventArgs e)
    {
        // sender is the StudentCard (same as Card_DoubleTapped); its DataContext is the tile VM.
        if (sender is Control { DataContext: StudentTileViewModel vm })
            StudentCommandRequested?.Invoke((vm, e.Command));
    }
}
