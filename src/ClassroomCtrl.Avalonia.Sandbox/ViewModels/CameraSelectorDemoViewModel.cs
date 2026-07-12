using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Sandbox demo VM for the ported CameraSelectorDialog (Phase 25.7-B). The shipped
/// dialog populated the device combo from code-behind and read the resolution/fps
/// ComboBoxItems by index; here the device list + selected indices bind to the VM
/// and Start/Cancel stamp LastAction so the effect is visible.
/// </summary>
public partial class CameraSelectorDemoViewModel : ObservableObject
{
    /// <summary>Enumerated capture devices (stand-in for AVFoundation discovery).</summary>
    public ObservableCollection<string> Devices { get; } = new()
    {
        "FaceTime HD Camera",
        "Logitech C920 (USB)",
        "OBS Virtual Camera",
    };

    [ObservableProperty] private int deviceIndex;

    /// <summary>0=320×240 · 1=640×480 · 2=1280×720 (shipped default = 640×480).</summary>
    [ObservableProperty] private int resolutionIndex = 1;

    /// <summary>0=5 · 1=10 · 2=15 · 3=30 fps (shipped default = 10).</summary>
    [ObservableProperty] private int fpsIndex = 1;

    [ObservableProperty] private string lastAction = "(no action yet)";

    [RelayCommand]
    private void Start()
    {
        var device = DeviceIndex >= 0 && DeviceIndex < Devices.Count ? Devices[DeviceIndex] : "(none)";
        var res = ResolutionIndex switch { 0 => "320×240", 2 => "1280×720", _ => "640×480" };
        var fps = FpsIndex switch { 0 => "5", 2 => "15", 3 => "30", _ => "10" };
        LastAction = $"Started: {device} · {res} @ {fps} fps";
    }

    [RelayCommand] private void Cancel() => LastAction = "Cancelled";
}
