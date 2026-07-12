using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Sandbox demo VM for the ported ApplyPolicyDialog (Phase 25.6-C). The shipped dialog
/// used x:Name + code-behind reading controls; here the policy toggles/inputs bind to
/// the VM (cleaner) and Apply/Revert/Cancel stamp LastAction so the effect is visible.
/// </summary>
public partial class ApplyPolicyDemoViewModel : ObservableObject
{
    [ObservableProperty] private bool blockUsb = true;
    [ObservableProperty] private bool blockOptical;
    [ObservableProperty] private bool blockPrint;
    [ObservableProperty] private bool blockApps = true;
    [ObservableProperty] private bool blockSites = true;

    [ObservableProperty] private string appsList = "chrome.exe, game.exe";
    [ObservableProperty] private string hostsList = "facebook.com, tiktok.com";

    /// <summary>0=No limit · 1=15 min · 2=30 min · 3=1 hr · 4=2 hr.</summary>
    [ObservableProperty] private int durationIndex;

    [ObservableProperty] private string lastAction = "(no action yet)";

    [RelayCommand]
    private void Apply()
    {
        var on = new List<string>();
        if (BlockUsb) on.Add("USB");
        if (BlockOptical) on.Add("CD/DVD");
        if (BlockPrint) on.Add("Print");
        if (BlockApps) on.Add("Apps");
        if (BlockSites) on.Add("Sites");
        var dur = DurationIndex switch { 1 => "15 min", 2 => "30 min", 3 => "1 hr", 4 => "2 hr", _ => "no limit" };
        LastAction = on.Count == 0 ? $"Applied: (none) · {dur}" : $"Applied: {string.Join(", ", on)} · {dur}";
    }

    [RelayCommand]
    private void Revert()
    {
        BlockUsb = BlockOptical = BlockPrint = BlockApps = BlockSites = false;
        AppsList = HostsList = "";
        DurationIndex = 0;
        LastAction = "Reverted all";
    }

    [RelayCommand] private void Cancel() => LastAction = "Cancelled";
}
