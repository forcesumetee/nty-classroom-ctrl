using ClassroomCtrl.Networking;
using System.Net;
using System.Windows;

namespace ClassroomCtrl.Student.Agent.Setup;

public partial class TeacherIPDialog : Window
{
    public bool Saved { get; private set; }

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

        if (!IPAddress.TryParse(text, out _))
        {
            ErrorText.Text = "Invalid IP address format. Example: 192.168.1.10";
            return;
        }

        try
        {
            TeacherIPConfig.Write(text);
            Saved = true;
            DialogResult = true;
            Close();
        }
        catch (System.UnauthorizedAccessException)
        {
            ErrorText.Text = "Cannot save. Please run this application as Administrator.";
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