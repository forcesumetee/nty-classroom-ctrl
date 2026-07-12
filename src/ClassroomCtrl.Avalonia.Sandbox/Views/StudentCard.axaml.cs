using Avalonia.Controls;
using Avalonia.Input;
using ClassroomCtrl.Avalonia.Sandbox.ViewModels;

namespace ClassroomCtrl.Avalonia.Sandbox.Views;

/// <summary>
/// PORTED from Teacher/Controls/StudentCard.xaml.cs (Phase 25.4).
///
/// WPF used code-behind right-click to stash the Window's DataContext onto the
/// Border.Tag so the ContextMenu (separate visual tree) could reach MainViewModel.
/// Avalonia opens a ContextMenu/ContextFlyout on right-click automatically and the
/// menu inherits scope from the target — so that Tag-stash hack is NOT needed here
/// (added + verified in 25.4-B/C). Left-click still toggles selection.
/// </summary>
public partial class StudentCard : UserControl
{
    public StudentCard()
    {
        InitializeComponent();
    }

    private void Card_Pressed(object? sender, PointerPressedEventArgs e)
    {
        // Only react to a left-press on the card body (inner buttons handle their own).
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && DataContext is StudentCardDemoViewModel vm)
        {
            vm.IsSelected = !vm.IsSelected;
        }
    }
}
