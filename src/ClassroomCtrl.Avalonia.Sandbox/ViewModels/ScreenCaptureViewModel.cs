using System;
using System.Threading.Tasks;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Phase 27-A — drives the Screen Capture tab. 27-A-2: permission flow only
/// (Check / Request + status). 27-A-3 adds the live frame image + capture stats.
/// </summary>
public partial class ScreenCaptureViewModel : ObservableObject
{
    private readonly ScreenCaptureService _capture = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusClass))]
    private string permissionStatus = "Unknown";

    [ObservableProperty] private bool isGranted;

    [ObservableProperty] private string hint =
        "Screen Recording permission is required to capture the display.";

    /// <summary>Style-class hook for the status pill.</summary>
    public string StatusClass => IsGranted ? "granted" : "denied";

    public ScreenCaptureViewModel()
    {
        if (!ScreenCaptureService.IsSupported)
        {
            PermissionStatus = "Unsupported (macOS only)";
            Hint = "The native capture helper is macOS-only; run on macOS to test.";
            return;
        }
        Refresh(_capture.CheckPermission());
    }

    [RelayCommand]
    private void CheckPermission() => Refresh(_capture.CheckPermission());

    [RelayCommand]
    private async Task RequestPermission()
    {
        var result = await _capture.RequestPermissionAsync();
        Refresh(result);
        if (result != ScreenPermission.Granted)
        {
            Hint = "If the prompt appeared: grant Screen Recording in System Settings › "
                 + "Privacy & Security, then RELAUNCH the app (the grant only takes effect "
                 + "on next launch). Then press Check.";
        }
        else
        {
            Hint = "Screen Recording granted. (Capture pipeline lands in Phase 27-A-3.)";
        }
    }

    private void Refresh(ScreenPermission p)
    {
        IsGranted = p == ScreenPermission.Granted;
        PermissionStatus = IsGranted ? "Granted" : "Not granted";
        OnPropertyChanged(nameof(StatusClass));
    }
}
