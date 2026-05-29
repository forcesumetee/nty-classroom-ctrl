using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClassroomCtrl.Shared.Wpf.Conference;

/// <summary>
/// Phase 15-D step 1 — Meet-style bottom toolbar slotted into ConferenceView.
/// Commands route through the host view-model (the parent DataContext).
/// The ⋮ More button opens a Popup declared as a Button resource.
///
/// Phase 16-B step 4 — moved from Teacher/Views/Conference/ to Shared.Wpf.
///
/// Phase 16-B+ step 2 — UI bug fix.  The Popup is now a sibling in the
/// visual tree (wrapped with the More button in a Grid), so the
/// MorePopup field is generated normally from x:Name.  The reaction
/// buttons use Click handlers + Tag instead of Command binding so the
/// path is robust even on shells (Student) whose VM may not expose
/// SendReactionCommand yet — we resolve it dynamically against the
/// current DataContext.
/// </summary>
public partial class ConferenceToolbar : UserControl
{
    public ConferenceToolbar()
    {
        InitializeComponent();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        MorePopup.IsOpen = !MorePopup.IsOpen;
    }

    private void ReactionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string emoji) return;
        MorePopup.IsOpen = false;

        // Resolve SendReactionCommand against the toolbar's DataContext
        // (the host shell VM — MainViewModel on Teacher,
        // StudentConferenceShellViewModel on Student).  Reflection avoids
        // taking a hard dep on a shared interface — Phase 16-D introduces
        // IConferenceShellViewModel and we can swap to a direct cast then.
        if (DataContext == null) return;
        var prop = DataContext.GetType().GetProperty("SendReactionCommand");
        if (prop?.GetValue(DataContext) is not ICommand cmd) return;
        if (cmd.CanExecute(emoji)) cmd.Execute(emoji);
    }
}
