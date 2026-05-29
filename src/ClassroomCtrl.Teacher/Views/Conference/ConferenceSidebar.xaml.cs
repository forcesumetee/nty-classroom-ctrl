using System.Windows;
using System.Windows.Controls;
using ClassroomCtrl.Teacher.ViewModels;

namespace ClassroomCtrl.Teacher.Views.Conference;

/// <summary>
/// Phase 15-D step 2 — slide-in sidebar overlay for ConferenceView.  Hosts
/// Chat (step 2) and Participants (step 3) tabs.  Lives as an overlay on the
/// right side of the gallery so toggling the sidebar doesn't reflow the
/// tiles.
/// </summary>
public partial class ConferenceSidebar : UserControl
{
    public ConferenceSidebar()
    {
        InitializeComponent();
    }

    /// <summary>Close X — pulls IsConferenceSidebarVisible to false on the
    /// host MainViewModel.  Avoids cycling through the toggle command
    /// (which has special "open to Chat" semantics that would surprise the
    /// user when closing from the Participants tab).</summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
        {
            vm.IsConferenceSidebarVisible = false;
        }
    }
}
