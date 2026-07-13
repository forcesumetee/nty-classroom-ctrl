using Avalonia.Controls;
using Avalonia.Input;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;

namespace ClassroomCtrl.Avalonia.Teacher.Views;

public partial class StudentCard : UserControl
{
    public StudentCard() => InitializeComponent();

    // TT-2-C — visual single-selection, from the Sandbox shell: a click toggles
    // the tile's IsSelected (the .selected border reacts). Bulk/multi-select is later.
    private void Card_Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is StudentTileViewModel vm)
            vm.IsSelected = !vm.IsSelected;
    }
}
