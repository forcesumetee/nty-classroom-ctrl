using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Sandbox demo VM for the ported TeacherIPDialog (Phase 25.7-C, first Student-side
/// view). Ports the shipped validation verbatim: an IPv4 dotted-quad regex AND
/// <see cref="IPAddress.TryParse"/> (belt-and-suspenders — TryParse alone
/// permissively accepts "10.0.0" and octal "0700.0.0.1", so the regex pins the
/// canonical four-decimal-octet form). The shipped code persisted via
/// TeacherIPConfig.Write; here Save just stamps LastAction / ErrorMessage.
/// </summary>
public partial class TeacherIpDemoViewModel : ObservableObject
{
    // Ported from TeacherIPDialog.xaml.cs (Phase 8 Section C).
    private static readonly Regex Ipv4Regex = new(
        @"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$",
        RegexOptions.Compiled);

    [ObservableProperty] private string teacherIp = "";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private string lastAction = "(no action yet)";

    [RelayCommand]
    private void Save()
    {
        var text = (TeacherIp ?? "").Trim();

        if (!Ipv4Regex.IsMatch(text) || !IPAddress.TryParse(text, out _))
        {
            ErrorMessage = "Please enter a valid IPv4 address (e.g. 192.168.1.10)";
            return;
        }

        ErrorMessage = "";
        LastAction = $"Saved teacher IP: {text}";
    }

    [RelayCommand]
    private void Cancel()
    {
        ErrorMessage = "";
        LastAction = "Cancelled";
    }
}
