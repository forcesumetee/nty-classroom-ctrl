using System;
using Avalonia.Controls;
using Avalonia.Input;
using ClassroomCtrl.Avalonia.Teacher.Services;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;
using ClassroomCtrl.Teacher.Core;

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

    // TT-6-B — decode the macOS selection idiom from the click's modifiers and drive the
    // grid's selection model. ⌘ = Meta on macOS. Range needs Shift; toggle needs ⌘; plain
    // click selects only this tile.
    private void Card_SelectionRequested(object? sender, SelectionRequestedEventArgs e)
    {
        if (sender is Control { DataContext: StudentTileViewModel vm } && DataContext is MainWindowViewModel mw)
        {
            bool cmd = e.Modifiers.HasFlag(KeyModifiers.Meta);
            bool shift = e.Modifiers.HasFlag(KeyModifiers.Shift);
            mw.Grid.HandleClick(vm.EndpointId, cmd, shift);
        }
    }

    // TT-6-B — ⌘A selects all, Esc clears. Handled at the window so they work regardless of
    // which tile (if any) has focus.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (DataContext is MainWindowViewModel mw)
        {
            if (e.Key == Key.A && e.KeyModifiers.HasFlag(KeyModifiers.Meta))
            {
                mw.Grid.SelectAllCommand.Execute(null);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                mw.Grid.ClearSelectionCommand.Execute(null);
                e.Handled = true;
            }
        }
        base.OnKeyDown(e);
    }
}
