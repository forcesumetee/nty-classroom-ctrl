using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ClassroomCtrl.Avalonia.Teacher.Services;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;
using ClassroomCtrl.Teacher.Core;

namespace ClassroomCtrl.Avalonia.Teacher.Views;

public partial class StudentCard : UserControl
{
    /// <summary>TT-5-B — raised when a context-menu action is chosen on this tile. The
    /// window wires this (like TT-3's DoubleTapped) and resolves the target student from
    /// the sender's DataContext; the card stays unaware of the command controller.</summary>
    public event EventHandler<StudentCommandEventArgs>? CommandRequested;

    public StudentCard() => InitializeComponent();

    // TT-2-C — visual single-selection, from the Sandbox shell: a click toggles
    // the tile's IsSelected (the .selected border reacts). Bulk/multi-select is later.
    private void Card_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is StudentTileViewModel vm)
            vm.IsSelected = !vm.IsSelected;
    }

    // TT-5-B — each command MenuItem carries its StudentCommand name in Tag; one handler
    // parses it and raises CommandRequested. Keeps the AXAML declarative and avoids a
    // handler per item.
    private void CommandItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<StudentCommand>(tag, out var cmd))
            CommandRequested?.Invoke(this, new StudentCommandEventArgs(cmd));
    }
}
