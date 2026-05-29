using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ClassroomCtrl.Shared.Wpf.Conference;

/// <summary>
/// Phase 15-D step 1 — Meet-style bottom toolbar slotted into ConferenceView.
/// Commands route through the host view-model (the parent DataContext).
/// The ⋮ More button opens a Popup declared as a Button resource.
///
/// Phase 16-B step 4 — moved from Teacher/Views/Conference/ to Shared.Wpf.
/// </summary>
public partial class ConferenceToolbar : UserControl
{
    public ConferenceToolbar()
    {
        InitializeComponent();
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.Resources["MorePopup"] is Popup popup)
        {
            popup.IsOpen = !popup.IsOpen;
        }
    }
}
