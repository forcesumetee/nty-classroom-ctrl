using ClassroomCtrl.Shared.Localization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomCtrl.Teacher.Dialogs;

public partial class LanguageDialog : Window
{
    public string? SelectedLanguage { get; private set; }

    public LanguageDialog()
    {
        InitializeComponent();

        // Populate list with display names; tag with ISO code
        foreach (var kv in Loc.SupportedLanguages)
        {
            LanguageList.Items.Add(new ListBoxItem
            {
                Content = kv.Value,         // display name (e.g. "ไทย")
                Tag = kv.Key,               // ISO code (e.g. "th")
                Padding = new Thickness(12, 8, 12, 8),
            });
        }

        // Pre-select current language
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