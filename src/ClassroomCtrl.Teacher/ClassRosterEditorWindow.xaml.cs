using ClassroomCtrl.Shared.Models.Roster;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;

namespace ClassroomCtrl.Teacher;

public partial class ClassRosterEditorWindow : Window
{
    private readonly ClassRoster _roster;
    private readonly ObservableCollection<EnrolledStudent> _students;

    public ClassRosterEditorWindow(ClassRoster roster)
    {
        InitializeComponent();
        _roster = roster;

        ClassNameBox.Text = roster.ClassName;
        DescriptionBox.Text = roster.Description;
        _students = new ObservableCollection<EnrolledStudent>(roster.Students);
        StudentsGrid.ItemsSource = _students;
    }

    private void AddFromConnected_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current?.MainWindow?.DataContext is not ViewModels.MainViewModel vm) return;
        foreach (var s in vm.Students)
        {
            if (string.IsNullOrEmpty(s.MachineName)) continue;
            // Avoid duplicates by MachineName
            if (_students.Any(x => string.Equals(x.MachineName, s.MachineName, System.StringComparison.OrdinalIgnoreCase))) continue;
            _students.Add(new EnrolledStudent
            {
                FullName = s.DisplayName,
                MachineName = s.MachineName,
            });
        }
    }

    private void AddRow_Click(object sender, RoutedEventArgs e)
    {
        _students.Add(new EnrolledStudent());
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (StudentsGrid.SelectedItem is EnrolledStudent es)
            _students.Remove(es);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _roster.ClassName = ClassNameBox.Text.Trim();
        _roster.Description = DescriptionBox.Text.Trim();
        _roster.Students = _students.ToList();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
