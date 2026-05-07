using ClassroomCtrl.Licensing;
using ClassroomCtrl.Shared.Localization;
using System;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomCtrl.Teacher.Activation;

public partial class ActivationWindow : Window
{
    public bool ActivationSucceeded { get; private set; }

    /// <summary>Tracks the most recent imperatively-set status so we can re-render it on language change.</summary>
    private (string Key, object[] Args, bool IsError)? _status;

    public ActivationWindow()
    {
        InitializeComponent();
        MachineCodeBox.Text = MachineCodeGenerator.Generate();

        // Populate language picker. Pre-select current language and disable change handler
        // during the initial population so it doesn't fire SetLanguage on initial assignment.
        foreach (var kv in Loc.SupportedLanguages)
        {
            LanguageBox.Items.Add(new ComboBoxItem
            {
                Content = kv.Value,
                Tag = kv.Key,
            });
        }
        var current = Loc.CurrentLanguage;
        foreach (ComboBoxItem item in LanguageBox.Items)
        {
            if ((string?)item.Tag == current)
            {
                LanguageBox.SelectedItem = item;
                break;
            }
        }

        Loc.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => Loc.LanguageChanged -= OnLanguageChanged;
    }

    private void LanguageBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is ComboBoxItem item && item.Tag is string code && code != Loc.CurrentLanguage)
        {
            Loc.SetLanguage(code);
            App.SavePreferredLanguage(code);
        }
    }

    private void OnLanguageChanged()
    {
        // Imperatively-set StatusText.Text doesn't auto-bind to DynamicResource, so re-render
        // it manually so the user sees error/info messages in the language they just picked.
        if (_status is { } s)
        {
            StatusText.Text = s.Args.Length > 0 ? Loc.Format(s.Key, s.Args) : Loc.Get(s.Key);
            StatusText.Foreground = s.IsError
                ? System.Windows.Media.Brushes.IndianRed
                : System.Windows.Media.Brushes.Green;
        }
    }

    private void SetStatus(string key, bool isError, params object[] args)
    {
        _status = (key, args, isError);
        StatusText.Text = args.Length > 0 ? Loc.Format(key, args) : Loc.Get(key);
        StatusText.Foreground = isError
            ? System.Windows.Media.Brushes.IndianRed
            : System.Windows.Media.Brushes.Green;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(MachineCodeBox.Text);
        SetStatus("Msg_MachineCodeCopied", isError: false);
    }

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            SetStatus("Err_LicenseKeyEmpty", isError: true);
            return;
        }

        var mc = MachineCodeBox.Text;
        if (!LicenseValidator.Validate(mc, key))
        {
            SetStatus("Err_LicenseKeyInvalid", isError: true);
            return;
        }

        try
        {
            LicenseStore.Save(new LicenseStore.StoredLicense(
                Key: key.ToUpperInvariant(),
                MachineCode: mc,
                ActivatedAtUtc: DateTime.UtcNow,
                Edition: "Standard"));
            ActivationSucceeded = true;
            DialogResult = true;
            Close();
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus("Err_LicenseWriteFailed", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus("Err_LicenseStorageError", isError: true, ex.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
