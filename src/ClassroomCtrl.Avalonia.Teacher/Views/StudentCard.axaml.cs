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

    /// <summary>TT-6-B — raised on a LEFT-click, carrying the keyboard modifiers. The window
    /// decodes them into the macOS selection idiom and drives the grid's selection model
    /// (IsSelected is now owned by that model, not toggled here).</summary>
    public event EventHandler<SelectionRequestedEventArgs>? SelectionRequested;

    public StudentCard() => InitializeComponent();

    // TT-6-B — a left-click requests selection (the window applies the macOS idiom via the
    // grid model). Right-click is left to the ContextMenu and does NOT change selection.
    private void Card_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            SelectionRequested?.Invoke(this, new SelectionRequestedEventArgs(e.KeyModifiers));
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
