using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ClassroomCtrl.Teacher.Views.Conference;

/// <summary>
/// Phase 15-D step 1 — Meet-style bottom toolbar slotted into ConferenceView.
/// Commands route through MainViewModel (the parent DataContext).  The ⋮ More
/// button opens a Popup declared as a Button resource; Phase 15-E step 4
/// flushes the popup with the 5-emoji reaction picker.
/// </summary>
public partial class ConferenceToolbar : UserControl
{
    public ConferenceToolbar()
    {
        InitializeComponent();
    }

    /// <summary>Phase 15-D — placeholder popup open.  Real picker UI lands in
    /// Phase 15-E step 4; today we just toggle the popup so the wiring path
    /// is verified during step 1 acceptance.</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoreButton.Resources["MorePopup"] is Popup popup)
        {
            popup.IsOpen = !popup.IsOpen;
        }
    }
}
