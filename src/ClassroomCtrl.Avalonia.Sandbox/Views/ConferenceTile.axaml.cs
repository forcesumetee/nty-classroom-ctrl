using Avalonia;
using Avalonia.Controls;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Avalonia.Sandbox.ViewModels;

namespace ClassroomCtrl.Avalonia.Sandbox.Views;

/// <summary>
/// PORTED from Shared.Wpf/Conference/ConferenceTile.xaml.cs (Phase 24.3).
///
/// WPF→Avalonia code-behind translation notes:
///   • DependencyProperty (SelfSuffix) → Avalonia StyledProperty (below). The
///     shape is analogous: a static registration + a CLR wrapper using
///     GetValue/SetValue.  Avalonia's AvaloniaProperty.Register is generic and
///     needs no PropertyMetadata for a simple default.
///   • DataContextChanged: Avalonia exposes the same event on Control; we
///     recompute the localized "(You)" suffix there, same as WPF.
///   • The reaction float-up Storyboard is DEFERRED — see TODO marker below.
///   • The UserControl_SizeChanged 16:9 aspect-fit hack is intentionally NOT
///     ported: the sandbox sizes tiles explicitly, so there's nothing to fit.
/// </summary>
public partial class ConferenceTile : UserControl
{
    // WPF: public static readonly DependencyProperty SelfSuffixProperty =
    //          DependencyProperty.Register(nameof(SelfSuffix), typeof(string), ...);
    public static readonly StyledProperty<string> SelfSuffixProperty =
        AvaloniaProperty.Register<ConferenceTile, string>(nameof(SelfSuffix), "");

    public string SelfSuffix
    {
        get => GetValue(SelfSuffixProperty);
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
        SelfSuffix = DataContext is ConferenceTileViewModel { IsSelf: true }
            ? Loc.Get("Conf_SelfSuffix", "(You)")
            : "";
    }

    // TODO(Phase 25 or later): port WPF Storyboard reaction float-up
    //   Original: Shared.Wpf/Conference/ConferenceTile.xaml.cs StartReactionFloat()
    //             (lines ~78-99) + ConferenceTile.xaml ReactionFloater (lines ~223-234)
    //   Uses: TranslateTransform.Y 20 → -80 + Opacity keyframe fade, ~2.8s duration,
    //         started from code-behind on CurrentReactionEmoji change.
    //   Avalonia equivalent: Styles/Animations (Animation class with KeyFrames, or a
    //         Transitions + class toggle).  Reaction currently renders as a static
    //         centered glyph (see ReactionFloater in ConferenceTile.axaml).
    //   Effort estimate: ~30-45 min (separate animation subsystem to learn).
}
