using System;
using System.Collections.ObjectModel;
using ClassroomCtrl.Shared.Wpf.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Student.Agent.ViewModels;

/// <summary>
/// Phase 16-B step 7 — student-side host VM for the Shared.Wpf Conference
/// gallery + toolbar + sidebar.  Tier 1 minimum: just enough wiring to
/// make the gallery render, the sidebar toggle work, and the End/Leave
/// button close the window.  Mic / cam / share / chat / reaction commands
/// land in Phase 16-C (peer cam) and 16-D (full role-aware shell).
///
/// This VM intentionally implements only <see cref="IConferenceSidebarHost"/>
/// at the interface level — the full <c>IConferenceShellViewModel</c>
/// contract arrives in Phase 16-D when role-gated UI lands.  Today the
/// toolbar's bindings resolve at runtime by name (WPF DataContext), so
/// commands that don't yet exist on this VM render as disabled buttons
/// in the student window — acceptable for the 16-B foundation gate.
/// </summary>
public partial class StudentConferenceShellViewModel : ObservableObject, IConferenceSidebarHost
{
    public Guid SessionId { get; }
    public Guid TeacherEndpointId { get; }

    public ConferenceGalleryViewModel ConferenceGallery { get; } = new();

    // Placeholder Students collection so the sidebar's Participants tab
    // binds cleanly.  Phase 16-C populates this with peer state.
    public ObservableCollection<object> Students { get; } = new();

    // Sidebar overlay state — mirrors the Teacher.MainViewModel shape so
    // the same ConferenceSidebar XAML works against either DataContext.
    [ObservableProperty] private bool isConferenceSidebarVisible;
    [ObservableProperty] private int conferenceSidebarTabIndex;
    public bool IsConferenceChatTabSelected         => ConferenceSidebarTabIndex == 0;
    public bool IsConferenceParticipantsTabSelected => ConferenceSidebarTabIndex == 1;

    partial void OnConferenceSidebarTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsConferenceChatTabSelected));
        OnPropertyChanged(nameof(IsConferenceParticipantsTabSelected));
    }

    // Sidebar / toolbar commands the shared XAML binds by name.  Phase 16-D
    // introduces IConferenceShellViewModel which both Teacher and Student
    // implement; today these are the minimal subset needed to keep the
    // 16-B gallery surface usable.
    public IRelayCommand ToggleConferenceSidebarCommand { get; }
    public IRelayCommand SelectConferenceChatTabCommand { get; }
    public IRelayCommand SelectConferenceParticipantsTabCommand { get; }
    public IRelayCommand ShowConferenceHandQueueCommand { get; }
    public IRelayCommand EndConferenceCommand { get; }

    /// <summary>Phase 16-B step 7 — fired when the student clicks End / Leave.
    /// ConferenceGalleryWindow subscribes and closes itself.</summary>
    public event EventHandler? RequestClose;

    public StudentConferenceShellViewModel(Guid sessionId, Guid teacherEndpointId, string hostName)
    {
        SessionId = sessionId;
        TeacherEndpointId = teacherEndpointId;

        // Seed the gallery with the teacher's tile so the very first frame
        // can land on the right participant slot.  Phase 16-C peer cam
        // wiring adds tiles for other students as their
        // ConferenceCameraStart envelopes arrive.
        ConferenceGallery.Tiles.Add(new ConferenceTileViewModel(
            teacherEndpointId,
            string.IsNullOrWhiteSpace(hostName) ? "Host" : hostName,
            isSelf: false));

        ToggleConferenceSidebarCommand = new RelayCommand(() =>
        {
            if (IsConferenceSidebarVisible && IsConferenceChatTabSelected)
            {
                IsConferenceSidebarVisible = false;
            }
            else
            {
                ConferenceSidebarTabIndex = 0;
                IsConferenceSidebarVisible = true;
            }
        });
        SelectConferenceChatTabCommand = new RelayCommand(() =>
        {
            if (IsConferenceSidebarVisible) ConferenceSidebarTabIndex = 0;
        });
        SelectConferenceParticipantsTabCommand = new RelayCommand(() =>
        {
            if (IsConferenceSidebarVisible) ConferenceSidebarTabIndex = 1;
        });
        ShowConferenceHandQueueCommand = new RelayCommand(() =>
        {
            ConferenceSidebarTabIndex = 1;
            IsConferenceSidebarVisible = true;
        });
        // Student "End" semantically means Leave for now (16-D adds the role
        // model that flips the label + verb).  Just close our window.
        EndConferenceCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));
    }
}
