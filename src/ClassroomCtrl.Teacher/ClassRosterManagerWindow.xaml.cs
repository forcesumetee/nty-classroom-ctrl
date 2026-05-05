using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Models.Roster;
using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace ClassroomCtrl.Teacher;

public partial class ClassRosterManagerWindow : Window
{
    public ClassRosterManagerWindow()
    {
        InitializeComponent();
        if (App.Roster != null)
            RosterList.ItemsSource = App.Roster.Rosters;
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var roster = new ClassRoster { ClassName = "New Class" };
        var editor = new ClassRosterEditorWindow(roster) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            App.Roster?.SaveRoster(roster);
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (RosterList.SelectedItem is not ClassRoster roster) return;
        var editor = new ClassRosterEditorWindow(roster) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            App.Roster?.SaveRoster(roster);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (RosterList.SelectedItem is not ClassRoster roster) return;
        var confirm = MessageBox.Show(
            Loc.Format("Confirm_DeleteRoster", roster.ClassName),
            Loc.Get("Btn_DeleteRoster"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes)
            App.Roster?.DeleteRoster(roster.Id);
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Roster JSON (*.json)|*.json",
            Title = Loc.Get("Btn_ImportRoster"),
        };
        if (dlg.ShowDialog() == true && App.Roster != null)
        {
            try
            {
                var json = File.ReadAllText(dlg.FileName);
                App.Roster.ImportFromJson(json);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message);
            }
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (RosterList.SelectedItem is not ClassRoster roster || App.Roster == null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Roster JSON (*.json)|*.json",
            FileName = $"{roster.ClassName}.json",
        };
        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, App.Roster.ExportToJson(roster));
        }
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (RosterList.SelectedItem is not ClassRoster roster) return;
        App.Roster?.SetActive(roster.Id);
        Close();
    }
}
