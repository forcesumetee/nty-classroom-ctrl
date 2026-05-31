using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClassroomCtrl.Teacher.ViewModels;

namespace ClassroomCtrl.Teacher.Controls;

/// <summary>
/// Phase 2 Section D — student tile UC.  Right-click captures the card's DataContext
/// (StudentViewModel) onto MainViewModel so AssignToRoomCommand can read it when the
/// user picks a room from the ContextMenu.  Same pattern the Phase 13 inline DataTemplate
/// used; ported here so the UC is self-contained.
/// </summary>
public partial class StudentCard : UserControl
{
    public StudentCard()
    {
        InitializeComponent();
    }

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is StudentViewModel student
            && Window.GetWindow(this)?.DataContext is MainViewModel vm)
        {
            vm.SetPendingAssignStudent(student);
        }
    }

    /// <summary>Phase 23 — left-click on a tile is the entry point for
    /// multi-select.  Inner Buttons (REC, 👁) consume their own
    /// MouseLeftButtonDown on the bubble path so this only fires when the
    /// click lands on the tile body itself.  Resolves Ctrl/Shift modifiers
    /// from the live Keyboard state and hands the decision to the
    /// MainViewModel selection state machine.</summary>
    private void Card_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is StudentViewModel student
            && Window.GetWindow(this)?.DataContext is MainViewModel vm)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            vm.SelectTile(student.EndpointId, ctrl, shift);
            e.Handled = true;
        }
    }
}
