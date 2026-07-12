using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// One roster row for the ported ClassRosterManager (Phase 25.7-A). Mirrors the
/// shipped GridView columns: ClassName / Description / Students.Count / LastUsedAt.
/// The shipped model exposed Students (a collection); here StudentCount stands in
/// so the column binds directly without a full student collection in the sandbox.
/// </summary>
public partial class ClassRoomRowDemoViewModel : ObservableObject
{
    [ObservableProperty] private string className = "";
    [ObservableProperty] private string description = "";
    [ObservableProperty] private int studentCount;
    [ObservableProperty] private string lastUsedAt = "";

    public ClassRoomRowDemoViewModel(string className, string description, int studentCount, string lastUsedAt)
    {
        ClassName = className;
        Description = description;
        StudentCount = studentCount;
        LastUsedAt = lastUsedAt;
    }
}

/// <summary>
/// Sandbox demo VM for the ported ClassRosterManager (Phase 25.7-A). The shipped
/// window used a code-behind ListView + Click handlers; here the rows bind to an
/// ObservableCollection and the footer buttons stamp LastAction so the effect is
/// visible in the sandbox (same evidence idiom as the StudentCard demo).
/// </summary>
public partial class RosterManagerDemoViewModel : ObservableObject
{
    public ObservableCollection<ClassRoomRowDemoViewModel> Rooms { get; } = new()
    {
        new("ม.4/1 Science", "Morning STEM cohort — Room 204", 32, "2026-07-11 09:15"),
        new("ม.4/2 Science", "Afternoon STEM cohort — Room 204", 30, "2026-07-10 13:40"),
        new("ม.5/3 English", "IELTS prep group", 24, "2026-07-08 10:05"),
        new("ม.6/1 Computing", "Lab A — programming block", 28, "2026-07-12 08:30"),
        new("Club: Robotics", "After-school enrichment", 16, "2026-06-28 15:20"),
    };

    /// <summary>Selected row (null when nothing is selected).</summary>
    [ObservableProperty] private ClassRoomRowDemoViewModel? selectedRoom;

    [ObservableProperty] private string lastAction = "(no action yet)";

    public RosterManagerDemoViewModel() => SelectedRoom = Rooms[0];

    private string Target => SelectedRoom?.ClassName ?? "(no selection)";

    [RelayCommand] private void Activate() => LastAction = $"Activated: {Target}";
    [RelayCommand] private void Open() => LastAction = $"Opened: {Target}";
    [RelayCommand] private void New() => LastAction = "New roster…";
    [RelayCommand] private void Delete() => LastAction = $"Deleted: {Target}";
    [RelayCommand] private void Import() => LastAction = "Import roster…";
    [RelayCommand] private void Export() => LastAction = $"Exported: {Target}";
}
