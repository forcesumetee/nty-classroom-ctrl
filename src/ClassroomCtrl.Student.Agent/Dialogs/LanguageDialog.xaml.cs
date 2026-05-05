using ClassroomCtrl.Shared.Localization;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomCtrl.Student.Agent.Dialogs;

public partial class LanguageDialog : Window
{
    public string? SelectedLanguage { get; private set; }

    public LanguageDialog()
    {
        InitializeComponent();

        foreach (var kv in Loc.SupportedLanguages)
        {
            LanguageList.Items.Add(new ListBoxItem
            {
                Content = kv.Value,
                Tag = kv.Key,
                Padding = new Thickness(12, 8, 12, 8),
            });
        }

        var current = Loc.CurrentLanguage;
        foreach (ListBoxItem item in LanguageList.Items)
        {
            if ((string?)item.Tag == current)
            {
                LanguageList.SelectedItem = item;
                break;
            }
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (LanguageList.SelectedItem is ListBoxItem item)
        {
            SelectedLanguage = item.Tag as string;
            DialogResult = true;
        }
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}