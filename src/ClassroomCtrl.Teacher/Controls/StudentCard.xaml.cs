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
}
