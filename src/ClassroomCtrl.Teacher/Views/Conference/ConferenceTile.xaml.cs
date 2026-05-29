using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Teacher.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomCtrl.Teacher.Views.Conference;

/// <summary>
/// Phase 15-C — single conference tile.  Hosts a <see cref="ConferenceTileViewModel"/>
/// via its DataContext.  Style triggers in XAML do the work; code-behind
/// only computes the localized "(You)" suffix for self-tiles since there's
/// no clean way to do that purely in XAML without another converter.
/// </summary>
public partial class ConferenceTile : UserControl
{
    public static readonly DependencyProperty SelfSuffixProperty =
        DependencyProperty.Register(nameof(SelfSuffix), typeof(string), typeof(ConferenceTile),
            new PropertyMetadata(""));

    public string SelfSuffix
    {
        get => (string)GetValue(SelfSuffixProperty);
        set => SetValue(SelfSuffixProperty, value);
    }

    public ConferenceTile()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RefreshSelfSuffix();
        RefreshSelfSuffix();
    }

    private void RefreshSelfSuffix()
    {
        if (DataContext is ConferenceTileViewModel vm && vm.IsSelf)
        {
            SelfSuffix = Loc.Get("Conf_SelfSuffix", "(You)");
        }
        else
        {
            SelfSuffix = "";
        }
    }
}
