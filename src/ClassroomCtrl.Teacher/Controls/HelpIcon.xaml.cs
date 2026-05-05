using System.Windows;
using System.Windows.Controls;
using ClassroomCtrl.Shared.Localization;

namespace ClassroomCtrl.Teacher.Controls;

/// <summary>
/// Phase 3 Section B — surfaces "What is this?" explanation for a section / feature.
/// XAML callers set TitleKey + BodyKey (Loc keys) and click reveals a Popup with both,
/// resolved against the current language.  Re-resolves on every click so language switches
/// take effect even on a long-lived UC instance.
/// </summary>
public partial class HelpIcon : UserControl
{
    public static readonly DependencyProperty TitleKeyProperty =
        DependencyProperty.Register(nameof(TitleKey), typeof(string), typeof(HelpIcon),
            new PropertyMetadata("", OnContentChanged));

    public static readonly DependencyProperty BodyKeyProperty =
        DependencyProperty.Register(nameof(BodyKey), typeof(string), typeof(HelpIcon),
            new PropertyMetadata("", OnContentChanged));

    public string TitleKey
    {
        get => (string)GetValue(TitleKeyProperty);
        set => SetValue(TitleKeyProperty, value);
    }

    public string BodyKey
    {
        get => (string)GetValue(BodyKeyProperty);
        set => SetValue(BodyKeyProperty, value);
    }

    public HelpIcon()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateContent();
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((HelpIcon)d).UpdateContent();

    private void UpdateContent()
    {
        TitleBlock.Text = string.IsNullOrEmpty(TitleKey) ? "" : Loc.Get(TitleKey);
        BodyBlock.Text  = string.IsNullOrEmpty(BodyKey)  ? "" : Loc.Get(BodyKey);
    }

    private void HelpBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateContent();
        HelpPopup.IsOpen = !HelpPopup.IsOpen;
    }
}
