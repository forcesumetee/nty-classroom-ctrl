using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Teacher.Services;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomCtrl.Teacher;

public partial class CameraSelectorDialog : Window
{
    public CameraSelectorDialog()
    {
        InitializeComponent();

        if (App.Camera != null)
        {
            var devices = App.Camera.EnumerateDevices();
            foreach (var d in devices)
                DeviceCombo.Items.Add(new ComboBoxItem { Content = d.Name, Tag = d.Moniker });
            if (DeviceCombo.Items.Count > 0)
                DeviceCombo.SelectedIndex = 0;
        }
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (App.Camera == null) return;
        if (DeviceCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string moniker)
        {
            MessageBox.Show(Loc.Get("Conf_NoWebcam", "No webcam detected on this PC"));
            return;
        }

        var (w, h) = ParseResolution(((ComboBoxItem?)ResolutionCombo.SelectedItem)?.Content?.ToString() ?? "640 × 480");
        var fps = int.TryParse(((ComboBoxItem?)FpsCombo.SelectedItem)?.Content?.ToString(), out var f) ? f : 10;

        if (App.Camera.Start(moniker, w, h, fps))
        {
            DialogResult = true;
            Close();
        }
        else
        {
            // Phase 14-B step 5 — surface the AForge / driver error verbatim
            // (e.g. "Camera held by another app", privacy permission denied)
            // instead of the generic Phase 9.5 message.
            var fmt = Loc.Get("Conf_CamStartFailFmt", "Failed to start camera: {0}");
            MessageBox.Show(string.Format(fmt, App.Camera.LastError));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static (int W, int H) ParseResolution(string s)
    {
        var parts = s.Replace(" ", "").Split('×');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
            return (w, h);
        return (640, 480);
    }
}
