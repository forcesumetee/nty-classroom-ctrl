using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomCtrl.Teacher.Dialogs;

public partial class ApplyPolicyDialog : Window
{
    public bool BlockUsbStorage { get; private set; }
    public bool BlockOpticalDrive { get; private set; }
    public bool BlockPrinting { get; private set; }
    public List<string> BlockedProcessNames { get; private set; } = new();
    public List<string> BlockedHostnames { get; private set; } = new();
    public bool RevertRequested { get; private set; }

    /// <summary>Duration in seconds before auto-revert. 0 = no expiry.</summary>
    public int DurationSeconds { get; private set; }

    // Initial values
    public bool InitialBlockUsbStorage { get; set; }
    public bool InitialBlockOpticalDrive { get; set; }
    public bool InitialBlockPrinting { get; set; }
    public List<string> InitialBlockedProcessNames { get; set; } = new();
    public List<string> InitialBlockedHostnames { get; set; } = new();

    public ApplyPolicyDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            UsbCheck.IsChecked = InitialBlockUsbStorage;
            OpticalCheck.IsChecked = InitialBlockOpticalDrive;
            PrintCheck.IsChecked = InitialBlockPrinting;
            AppsList.Text = string.Join(", ", InitialBlockedProcessNames);
            HostsList.Text = string.Join(", ", InitialBlockedHostnames);
            // Phase 1.1 fix-up — also restore the master toggle for Apps/Hosts. Apply_Click reads
            // these as the gate for whether to send the lists, so without this on reopen the user
            // would see the list text but a CLEARED checkbox, and clicking Apply again would broadcast
            // empty lists (effectively unblocking everything previously locked).
            AppsCheck.IsChecked = InitialBlockedProcessNames.Count > 0;
            HostsCheck.IsChecked = InitialBlockedHostnames.Count > 0;
        };
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        BlockUsbStorage = UsbCheck.IsChecked == true;
        BlockOpticalDrive = OpticalCheck.IsChecked == true;
        BlockPrinting = PrintCheck.IsChecked == true;

        BlockedProcessNames = (AppsCheck.IsChecked == true)
            ? ParseList(AppsList.Text) : new List<string>();
        BlockedHostnames = (HostsCheck.IsChecked == true)
            ? ParseList(HostsList.Text) : new List<string>();

        // Read duration from combo
        if (DurationCombo.SelectedItem is ComboBoxItem item && item.Tag is string tag
            && int.TryParse(tag, out int sec))
        {
            DurationSeconds = sec;
        }
        else
        {
            DurationSeconds = 0;
        }

        RevertRequested = false;
        DialogResult = true;
        Close();
    }

    private void Revert_Click(object sender, RoutedEventArgs e)
    {
        RevertRequested = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static List<string> ParseList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw
            .Split(new[] { ',', '\n', '\r', ';' }, System.StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }
}