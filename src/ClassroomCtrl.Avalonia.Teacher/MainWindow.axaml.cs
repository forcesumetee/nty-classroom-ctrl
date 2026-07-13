using System;
using Avalonia.Controls;
using Avalonia.Input;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;

namespace ClassroomCtrl.Avalonia.Teacher;

public partial class MainWindow : Window
{
    /// <summary>TT-3-B — raised when a student tile is double-tapped (the "view this
    /// student's screen" affordance while the context menu is deferred). App wires this
    /// to the ScreenViewController; MainWindow stays unaware of window management.</summary>
    public event Action<StudentTileViewModel>? StudentActivated;

    public MainWindow() => InitializeComponent();

    private void Card_DoubleTapped(object? sender, TappedEventArgs e)
    {
        // The handler is attached to the StudentCard element in the DataTemplate, so
        // sender is the card and its DataContext is the tile VM.
        if (sender is Control { DataContext: StudentTileViewModel vm })
            StudentActivated?.Invoke(vm);
    }
}
