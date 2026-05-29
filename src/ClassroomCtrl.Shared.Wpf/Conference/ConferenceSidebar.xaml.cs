using System.Windows;
using System.Windows.Controls;
using ClassroomCtrl.Shared.Wpf.ViewModels;

namespace ClassroomCtrl.Shared.Wpf.Conference;

/// <summary>
/// Phase 15-D step 2 — slide-in sidebar overlay for ConferenceView.  Hosts
/// Chat (step 2) and Participants (step 3) tabs.  Lives as an overlay on the
/// right side of the gallery so toggling the sidebar doesn't reflow the
/// tiles.
///
/// Phase 16-B step 4 — moved from Teacher/Views/Conference/ to Shared.Wpf.
/// Close X uses IConferenceSidebarHost instead of a direct MainViewModel
/// reference so the same control works on Student.Agent's shell.
/// </summary>
public partial class ConferenceSidebar : UserControl
{
    public ConferenceSidebar()
    {
        InitializeComponent();
    }

    /// <summary>Close X — pulls IsConferenceSidebarVisible to false via
    /// IConferenceSidebarHost.  Avoids cycling through the toggle command
    /// (which has special "open to Chat" semantics that would surprise the
    /// user when closing from the Participants tab).</summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is IConferenceSidebarHost host)
        {
            host.IsConferenceSidebarVisible = false;
        }
    }
}
