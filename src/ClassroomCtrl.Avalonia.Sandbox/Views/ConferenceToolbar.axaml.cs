using Avalonia.Controls;

namespace ClassroomCtrl.Avalonia.Sandbox.Views;

/// <summary>
/// PORTED from Shared.Wpf/Conference/ConferenceToolbar.xaml.cs (Phase 25.1).
/// Meet-style bottom toolbar; commands route through the host DataContext.
///
/// Dropped from the WPF version (Windows-only / not needed on macOS):
///   • LogEmojiDiagnostic() — a Win11 Thai-locale font-shaping probe using
///     FontFamily.GetTypefaces()/GlyphTypeface; irrelevant on macOS.
///   • pack:// PNG reaction assets — a Windows font workaround; the Avalonia
///     picker renders color emoji directly (Apple Color Emoji).
/// The reaction Flyout + selection wiring arrives in Phase 25.1-B.
/// </summary>
public partial class ConferenceToolbar : UserControl
{
    public ConferenceToolbar()
    {
        InitializeComponent();
    }
}
