using ClassroomCtrl.Avalonia.Sandbox.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Sandbox demo VM for the ported TeacherIPDialog (Phase 25.7-C, first Student-side
/// view). Validation lives in <see cref="IpValidation"/> (extracted in Phase 26.0 so
/// the connection flow reuses the identical rule): an IPv4 dotted-quad regex AND
/// IPAddress.TryParse (belt-and-suspenders). The shipped code persisted via
/// TeacherIPConfig.Write; here Save just stamps LastAction / ErrorMessage.
/// </summary>
public partial class TeacherIpDemoViewModel : ObservableObject
{
    [ObservableProperty] private string teacherIp = "";
    [ObservableProperty] private string errorMessage = "";
    [ObservableProperty] private string lastAction = "(no action yet)";

    [RelayCommand]
    private void Save()
    {
        var text = (TeacherIp ?? "").Trim();

        if (!IpValidation.IsValidIpv4(text))
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
