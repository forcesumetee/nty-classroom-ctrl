using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Sandbox host VM for the StudentCard port (Phase 25.4). Reproduces the MainViewModel
/// binding surface the card's ContextMenu routes to. Commands are INERT but each stamps
/// <see cref="LastAction"/> — visible proof that ContextMenu command routing resolved
/// AND fired (the 25.4-C empirical test), with the acted-on student as the parameter.
/// </summary>
public partial class StudentGridDemoViewModel : ObservableObject
{
    public ObservableCollection<StudentCardDemoViewModel> Students { get; } = new();

    /// <summary>Last command that fired, e.g. "Lock → Somchai". Displayed in the grid
    /// tab so a menu click's effect is visible; asserted in the headless routing test.</summary>
    [ObservableProperty] private string lastAction = "(no action yet)";

    public StudentGridDemoViewModel()
    {
        AddStudent(new StudentCardDemoViewModel
        {
            DisplayName = "Somchai", MachineName = "LAB-01",
            RoomBadgeVisible = true, RoomName = "Group A", RoomBadgeColorHex = "#4285F4",
            QualityDotColorHex = "#34A853", HostBadge = true, CanRecord = true,
            RecButtonText = "REC", RecButtonColorHex = "#EA4335", RecOpacity = 1.0,
        });
        AddStudent(new StudentCardDemoViewModel
        {
            DisplayName = "Ploy", MachineName = "LAB-02",
            HandRaised = true, Talking = true, QualityDotColorHex = "#FBBC04",
            CanRecord = false, RecButtonText = "REC", RecButtonColorHex = "#5F6368", RecOpacity = 0.5,
        });
        AddStudent(new StudentCardDemoViewModel
        {
            DisplayName = "Anong", MachineName = "LAB-03", IsSelected = true,
            PolicyBadgeVisible = true, QualityDotColorHex = "#EA4335",
            CanRecord = true, RecButtonText = "REC", RecButtonColorHex = "#EA4335", RecOpacity = 1.0,
        });
        AddStudent(new StudentCardDemoViewModel
        {
            DisplayName = "Kittipong", MachineName = "LAB-04",
            RoomBadgeVisible = true, RoomName = "Group B", RoomBadgeColorHex = "#7C3AED",
            QualityDotColorHex = "#34A853", CanRecord = true, RecButtonText = "REC",
            RecButtonColorHex = "#EA4335", RecOpacity = 1.0,
        });
    }

    /// <summary>Wire each item's Host back-reference so its ContextMenu can route
    /// commands via {Binding Host.XCommand} — required because $parent ancestor-walk
    /// does NOT cross the ContextMenu popup boundary (verified in 25.4-C).</summary>
    private void AddStudent(StudentCardDemoViewModel s)
    {
        s.Host = this;
        Students.Add(s);
    }

    private void Act(string verb, StudentCardDemoViewModel? s)
        => LastAction = $"{verb} → {(s?.DisplayName ?? "?")}";

    // Command surface the ContextMenu binds (names match the shipped MainViewModel).
    [RelayCommand] private void ViewStudentScreen(StudentCardDemoViewModel? s) => Act("View screen", s);
    [RelayCommand] private void LockOne(StudentCardDemoViewModel? s) => Act("Lock", s);
    [RelayCommand] private void UnlockOne(StudentCardDemoViewModel? s) => Act("Unlock", s);
    [RelayCommand] private void ApplyPolicyToStudent(StudentCardDemoViewModel? s) => Act("Apply policy", s);
    [RelayCommand] private void RevertPolicyForStudent(StudentCardDemoViewModel? s) => Act("Revert policy", s);
    [RelayCommand] private void OpenDMConversation(StudentCardDemoViewModel? s) => Act("Chat", s);
    [RelayCommand] private void SetAsHost(StudentCardDemoViewModel? s) => Act("Set as host", s);
    [RelayCommand] private void RemoveHost(StudentCardDemoViewModel? s) => Act("Remove host", s);
    [RelayCommand] private void StartDemo(StudentCardDemoViewModel? s) => Act("Start demo", s);
    [RelayCommand] private void StopDemo() => LastAction = "Stop demo";
    [RelayCommand] private void RemoveFromRoom(StudentCardDemoViewModel? s) => Act("Remove from room", s);
    [RelayCommand] private void LogoffOne(StudentCardDemoViewModel? s) => Act("Logoff", s);
    [RelayCommand] private void RestartOne(StudentCardDemoViewModel? s) => Act("Restart", s);
    [RelayCommand] private void ShutdownOne(StudentCardDemoViewModel? s) => Act("Shutdown", s);
    [RelayCommand] private void ToggleStudentRecording(StudentCardDemoViewModel? s) => Act("Toggle REC", s);
    // Static-submenu room assignment (Phase 25.4 scope: 2 hardcoded rooms).
    [RelayCommand] private void AssignToRoomA(StudentCardDemoViewModel? s) => Act("Assign → Group A", s);
    [RelayCommand] private void AssignToRoomB(StudentCardDemoViewModel? s) => Act("Assign → Group B", s);
}

/// <summary>Demo item VM. WPF exposed *Visibility (Visibility) members; Avalonia uses
/// bools → IsVisible. Dynamic colors stay as hex strings + HexToBrushConverter.</summary>
public partial class StudentCardDemoViewModel : ObservableObject
{
    /// <summary>Back-reference to the host VM. The ContextMenu inherits the item's
    /// DataContext, so menu items bind {Binding Host.XCommand} to reach host commands
    /// (popup-safe, unlike $parent which can't escape the popup — see 25.4-C).</summary>
    public StudentGridDemoViewModel? Host { get; set; }

    public string DisplayName { get; init; } = "";
    public string MachineName { get; init; } = "";
    [ObservableProperty] private bool isSelected;

    public bool RoomBadgeVisible { get; init; }
    public string RoomName { get; init; } = "";
    public string RoomBadgeColorHex { get; init; } = "#4285F4";
    public bool PolicyBadgeVisible { get; init; }
    public string QualityDotColorHex { get; init; } = "#34A853";
    public bool HandRaised { get; init; }
    public bool HostBadge { get; init; }
    public bool Talking { get; init; }

    public bool CanRecord { get; init; }
    public string RecButtonText { get; init; } = "REC";
    public string RecButtonColorHex { get; init; } = "#EA4335";
    public double RecOpacity { get; init; } = 1.0;
}
