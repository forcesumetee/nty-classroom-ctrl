using ClassroomCtrl.Networking;
using ClassroomCtrl.Shared.Localization;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;

namespace ClassroomCtrl.Student.Agent.Setup;

public partial class TeacherIPDialog : Window
{
    public bool Saved { get; private set; }

    // Phase 8 Section C — IPv4 dotted-quad regex.  Belt-and-suspenders against
    // IPAddress.TryParse, which permissively accepts forms like "10.0.0" and
    // octal "0700.0.0.1".  We only want the canonical four-decimal-octet form.
    private static readonly Regex Ipv4Regex = new(
        @"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$",
        RegexOptions.Compiled);

    public TeacherIPDialog()
    {
        InitializeComponent();

        var existing = TeacherIPConfig.Read();
        if (!string.IsNullOrEmpty(existing))
            IPTextBox.Text = existing;

        IPTextBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var text = IPTextBox.Text?.Trim() ?? "";

        if (!Ipv4Regex.IsMatch(text) || !IPAddress.TryParse(text, out _))
        {
            ErrorText.Text = $"{Loc.Get("Err_InvalidIP")} (e.g. 192.168.1.10)";
            return;
        }

        try
        {
            // Phase 8 Section B — TeacherIPConfig.Write now persists to
            // %ProgramData%\NTY\ClassroomCtrl\config.txt; no admin elevation
            // required, so the UnauthorizedAccessException catch below is a
            // legacy safety net for the rare write-protected ProgramData
            // (e.g. a locked-down GPO image).
            TeacherIPConfig.Write(text);
            Saved = true;
            DialogResult = true;
            Close();
        }
        catch (System.UnauthorizedAccessException)
        {
            ErrorText.Text = "Cannot write config.txt — folder is read-only. Contact IT.";
        }
        catch (System.Exception ex)
        {
            ErrorText.Text = $"Save failed: {ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}