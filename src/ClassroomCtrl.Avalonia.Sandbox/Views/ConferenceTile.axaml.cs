using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Styling;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Avalonia.Sandbox.ViewModels;

namespace ClassroomCtrl.Avalonia.Sandbox.Views;

/// <summary>
/// PORTED from Shared.Wpf/Conference/ConferenceTile.xaml.cs (Phase 24.3; reaction
/// float-up animation added Phase 25.1-C).
///
/// WPF→Avalonia code-behind translation notes:
///   • DependencyProperty (SelfSuffix) → Avalonia StyledProperty.
///   • DataContextChanged recomputes the localized "(You)" suffix + (re)attaches
///     the reaction listener, same as the WPF version's AttachReactionListener.
///   • Reaction float-up: WPF Storyboard (DoubleAnimationUsingKeyFrames on Opacity
///     + DoubleAnimation on TranslateTransform.Y, started via BeginAnimation) →
///     Avalonia Animation (KeyFrame/Cue/Setter) run via RunAsync — opacity on the
///     control, Y on a TranslateTransform Animatable. See StartReactionFloat().
/// </summary>
public partial class ConferenceTile : UserControl
{
    public static readonly StyledProperty<string> SelfSuffixProperty =
        AvaloniaProperty.Register<ConferenceTile, string>(nameof(SelfSuffix), "");

    public string SelfSuffix
    {
        get => GetValue(SelfSuffixProperty);
        set => SetValue(SelfSuffixProperty, value);
    }

    private ConferenceTileViewModel? _attached;

    public ConferenceTile()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { RefreshSelfSuffix(); AttachReactionListener(); };
        RefreshSelfSuffix();
        AttachReactionListener();
    }

    private void RefreshSelfSuffix()
    {
        SelfSuffix = DataContext is ConferenceTileViewModel { IsSelf: true }
            ? Loc.Get("Conf_SelfSuffix", "(You)")
            : "";
    }

    private void AttachReactionListener()
    {
        if (_attached != null) _attached.PropertyChanged -= OnTilePropertyChanged;
        _attached = DataContext as ConferenceTileViewModel;
        if (_attached != null) _attached.PropertyChanged += OnTilePropertyChanged;
    }

    private void OnTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConferenceTileViewModel.CurrentReactionEmoji)) return;
        if (sender is not ConferenceTileViewModel vm || string.IsNullOrEmpty(vm.CurrentReactionEmoji)) return;
        StartReactionFloat();
    }

    /// <summary>
    /// Phase 25.1-C — fulfils TODO(Phase 25 or later) from Phase 24.3.
    /// WPF original (ConferenceTile.xaml.cs StartReactionFloat): Opacity keyframes
    /// 0→1@15%→1@70%→0@100% + TranslateTransform.Y 20→-80, ~2.8s, via BeginAnimation.
    ///
    /// Avalonia GOTCHAS learned here (both documented in the cheat sheet):
    ///  1. Animation.RunAsync targets a Visual — running it on a bare
    ///     TranslateTransform throws InvalidCastException.
    ///  2. Animating RenderTransform via TransformOperations needs the
    ///     TransformOperationsAnimator, which is INTERNAL and only auto-registers
    ///     from the XAML path — unusable from a code-created Animation.
    /// So the rise animates Margin.Top instead (ThicknessAnimator is registered by
    /// default): the glyph is centered, so a +20→-80 top margin moves it up. Both
    /// Animations run on the control; FillMode.Forward holds the faded-out end.
    /// </summary>
    private void StartReactionFloat()
    {
        var dur = TimeSpan.FromSeconds(2.8);

        var fade = new Animation
        {
            Duration = dur,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d),    Setters = { new Setter(OpacityProperty, 0d) } },
                new KeyFrame { Cue = new Cue(0.15d), Setters = { new Setter(OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(0.7d),  Setters = { new Setter(OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1d),    Setters = { new Setter(OpacityProperty, 0d) } },
            },
        };

        var rise = new Animation
        {
            Duration = dur,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(MarginProperty, new Thickness(0, 20, 0, 0)) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(MarginProperty, new Thickness(0, -80, 0, 0)) } },
            },
        };

        _ = fade.RunAsync(ReactionFloater);
        _ = rise.RunAsync(ReactionFloater);
    }
}
