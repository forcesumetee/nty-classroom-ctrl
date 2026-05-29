namespace ClassroomCtrl.Shared.Wpf.ViewModels;

/// <summary>
/// Phase 16-B step 1 — minimal contract the Conference sidebar's close X
/// uses to flip its host VM's visibility flag without taking a hard
/// reference to either Teacher.MainViewModel or
/// Student.StudentConferenceShellViewModel.  Both shell VMs implement
/// this interface; the Shared.Wpf sidebar code-behind casts DataContext
/// to it.
///
/// The full IConferenceShellViewModel + IConferenceRole pair lands in
/// Phase 16-D (role-based UI gating); this 16-B foothold keeps the
/// Shared.Wpf surface minimal and the existing 15-D behaviour
/// unchanged.
/// </summary>
public interface IConferenceSidebarHost
{
    /// <summary>True when the sidebar overlay is visible.  Settable so the
    /// close X can pull it to false without invoking the toggle command
    /// (which has special "open to Chat" semantics that would surprise the
    /// user when closing from the Participants tab).</summary>
    bool IsConferenceSidebarVisible { get; set; }
}
