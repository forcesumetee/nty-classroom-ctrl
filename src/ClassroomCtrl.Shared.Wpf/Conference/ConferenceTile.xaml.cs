using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Wpf.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClassroomCtrl.Shared.Wpf.Conference;

/// <summary>
/// Phase 15-C — single conference tile.  Hosts a <see cref="ConferenceTileViewModel"/>
/// via its DataContext.  Style triggers in XAML do the work; code-behind
/// only computes the localized "(You)" suffix for self-tiles since there's
/// no clean way to do that purely in XAML without another converter.
///
/// Phase 15-E step 5 — code-behind also drives the reaction float-up
/// animation: subscribes to the tile VM's PropertyChanged + starts the
/// Storyboard when CurrentReactionEmoji becomes non-empty.  Storyboard
/// is local (Begin on the TextBlock) so multiple simultaneous reactions
/// across tiles don't share state.
///
/// Phase 16-B step 4 — moved from Teacher/Views/Conference/ to Shared.Wpf.
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

    private ConferenceTileViewModel? _attached;

    public ConferenceTile()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            RefreshSelfSuffix();
            AttachReactionListener();
        };
        RefreshSelfSuffix();
        AttachReactionListener();
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

    private void AttachReactionListener()
    {
        if (_attached != null) _attached.PropertyChanged -= OnTilePropertyChanged;
        _attached = DataContext as ConferenceTileViewModel;
        if (_attached != null) _attached.PropertyChanged += OnTilePropertyChanged;
    }

    private void OnTilePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConferenceTileViewModel.CurrentReactionEmoji)) return;
        if (sender is not ConferenceTileViewModel vm) return;
        if (string.IsNullOrEmpty(vm.CurrentReactionEmoji)) return;
        Dispatcher.Invoke(StartReactionFloat);
    }

    private void StartReactionFloat()
    {
        // Start position: 20 px below tile center, opacity 0.
        // End position:   80 px above tile center, opacity 0.
        // Peak: opacity 1 at ~20% of duration so the emoji is fully visible
        // for most of the 3-second window before fading.
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.0)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.15)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, System.Windows.Media.Animation.KeyTime.FromPercent(0.7)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, System.Windows.Media.Animation.KeyTime.FromPercent(1.0)));
        fade.Duration = new System.Windows.Duration(System.TimeSpan.FromSeconds(2.8));

        var rise = new DoubleAnimation
        {
            From = 20,
            To = -80,
            Duration = new System.Windows.Duration(System.TimeSpan.FromSeconds(2.8)),
        };
        ReactionFloater.BeginAnimation(OpacityProperty, fade);
        ReactionFloaterTransform.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    private void UserControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Phase 22.5-D: bidirectional proportional scaling.
        // We measure the available cell slot provided by the wrapper (minus Margin="8" x2).
        double slotW = Math.Max(0, e.NewSize.Width - 16);
        double slotH = Math.Max(0, e.NewSize.Height - 16);

        if (slotW <= 0 || slotH <= 0) return;

        double targetW = slotW;
        double targetH = slotW * 0.5625; // 16:9 expected height

        // If the cell is too short to fit 16:9 natively (e.g. wide layout), scale down by height (pillarbox)
        if (targetH > slotH)
        {
            targetH = slotH;
            targetW = slotH / 0.5625;
        }

        // Apply explicitly computed sizes to the inner container.
        // Since OuterBorder is hosted in a UserControl with Stretch alignment, 
        // explicitly setting its dimensions automatically centers it.
        OuterBorder.Width = targetW;
        OuterBorder.Height = targetH;
    }
}
