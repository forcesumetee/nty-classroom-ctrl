using Avalonia.Controls;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Sandbox;
using ClassroomCtrl.Avalonia.Sandbox.ViewModels;

namespace HeadlessCapture;

/// <summary>
/// A named, self-documenting capture scenario. Browsing this file shows the history
/// + rationale of every baseline (which phase produced it, which committed image it
/// corresponds to). Add one entry per future port that needs a screenshot.
/// </summary>
public sealed class Scenario
{
    /// <summary>Human-readable description of what the capture shows.</summary>
    public required string Description { get; init; }
    /// <summary>Phase that first produced this baseline (audit trail).</summary>
    public required string OriginalPhase { get; init; }
    /// <summary>Committed baseline image this scenario reproduces (repo-relative).</summary>
    public string? OriginalScreenshot { get; init; }
    public int Width { get; init; } = 940;
    public int Height { get; init; } = 1000;
    /// <summary>Builds the root Window to capture.</summary>
    public required Func<Window> BuildWindow { get; init; }
    /// <summary>Optional post-show hook: tab select, VM state injection, animation
    /// stepping — the "code, not strings" part of a capture.</summary>
    public Action<Window>? AfterShow { get; init; }
}

public static class Scenarios
{
    public static readonly IReadOnlyDictionary<string, Scenario> All =
        new Dictionary<string, Scenario>(StringComparer.OrdinalIgnoreCase)
    {
        ["theme"] = new()
        {
            Description = "Full ConferenceDarkTheme showcase (36 keys) — Sandbox tab 0. Static → deterministic.",
            OriginalPhase = "25.0-B",
            OriginalScreenshot = "docs/phase-25.0-B-theme.png",
            Width = 940, Height = 1000,
            BuildWindow = () => new MainWindow(),
            AfterShow = w => CaptureRunner.SelectTab(w, 0),
        },

        ["tile"] = new()
        {
            Description = "ConferenceTile states — Sandbox tab 1. Static → deterministic. "
                        + "(Original 24.3 baseline predates the TabControl: it was 760x520 with no "
                        + "tab strip, so this re-capture legitimately differs — see README.)",
            OriginalPhase = "24.3",
            OriginalScreenshot = "docs/phase-24.3-conferencetile.png",
            Width = 940, Height = 1000,
            BuildWindow = () => new MainWindow(),
            AfterShow = w => CaptureRunner.SelectTab(w, 1),
        },

        ["applypolicy"] = new()
        {
            Description = "Ported ApplyPolicyDialog — header/body/footer, ClassroomLightTheme "
                        + "+ design system (Button.*/Text.h*), CheckBox/ComboBox/TextBox — Sandbox tab 7.",
            OriginalPhase = "25.6",
            OriginalScreenshot = "docs/phase-25.6-applypolicy.png",
            Width = 620, Height = 760,
            BuildWindow = () => new MainWindow(),
            AfterShow = w => CaptureRunner.SelectTab(w, 7),
        },

        ["roster"] = new()
        {
            Description = "Ported ClassRosterManager — header/body/footer, ClassroomLightTheme "
                        + "+ design system, ListBox+Grid template (4 columns + aligned header row, "
                        + "cheat sheet §17) — Sandbox tab 8.",
            OriginalPhase = "25.7",
            OriginalScreenshot = "docs/phase-25.7-roster.png",
            Width = 900, Height = 700,
            BuildWindow = () => new MainWindow(),
            AfterShow = w => CaptureRunner.SelectTab(w, 8),
        },

        ["camera"] = new()
        {
            Description = "Ported CameraSelectorDialog — header/body/footer form, "
                        + "ClassroomLightTheme + design system, device ComboBox + 2-col "
                        + "resolution/fps grid — Sandbox tab 9.",
            OriginalPhase = "25.7",
            OriginalScreenshot = "docs/phase-25.7-camera.png",
            Width = 620, Height = 460,
            BuildWindow = () => new MainWindow(),
            AfterShow = w => CaptureRunner.SelectTab(w, 9),
        },

        ["teacherip"] = new()
        {
            Description = "Ported TeacherIPDialog (first Student-side view) — header/body/footer "
                        + "input dialog, ClassroomLightTheme + design system, TextBox + inline "
                        + "validation error (Accent.Danger). Captured MID-VALIDATION: an invalid "
                        + "IP was entered + Save fired so the error TextBlock shows — Sandbox tab 10.",
            OriginalPhase = "25.7",
            OriginalScreenshot = "docs/phase-25.7-teacherip.png",
            Width = 620, Height = 440,
            BuildWindow = () => new MainWindow(),
            AfterShow = w =>
            {
                CaptureRunner.SelectTab(w, 10);
                // Drive the validation path so the inline error is visible in the baseline.
                if (w.DataContext is MainWindowViewModel mvm)
                {
                    mvm.TeacherIp.TeacherIp = "192.168.1";      // invalid dotted-quad (only 3 octets)
                    mvm.TeacherIp.SaveCommand.Execute(null);
                }
                Dispatcher.UIThread.RunJobs();
            },
        },

        ["classroom"] = new()
        {
            Description = "Classroom LIGHT theme tokens (36) + icon showcase — Sandbox tab 6. "
                        + "Proves the ported Colors.Light theme + native emoji/Unicode icons.",
            OriginalPhase = "25.5",
            OriginalScreenshot = "docs/phase-25.5-classroom-theme.png",
            Width = 1000, Height = 1000,
            BuildWindow = () => new MainWindow(),
            AfterShow = w => CaptureRunner.SelectTab(w, 6),
        },

        ["studentcard"] = new()
        {
            Description = "StudentCard grid with the ContextMenu + dynamic 'Assign to "
                        + "room' submenu OPEN — Sandbox tab 5 (17 items + separators + "
                        + "ItemsSource-driven submenu).",
            OriginalPhase = "25.4",
            OriginalScreenshot = "docs/phase-25.4-studentcard-menu.png",
            Width = 1000, Height = 800,
            BuildWindow = () => new MainWindow(),
            AfterShow = w => { CaptureRunner.SelectTab(w, 5); CaptureRunner.OpenContextMenuWithSubmenu(w, "Assign to room"); },
        },

        ["sidebar"] = new()
        {
            Description = "Full Conference composition — tile grid + toolbar (bottom) + "
                        + "sidebar (right) — Sandbox tab 4. Static → deterministic content.",
            OriginalPhase = "25.3-C",
            OriginalScreenshot = "docs/phase-25.3-conference-full.png",
            Width = 1280, Height = 800,
            BuildWindow = () => new MainWindow(),
            AfterShow = w => CaptureRunner.SelectTab(w, 4),
        },

        ["bottombar"] = new()
        {
            Description = "Conference bottom bar + reaction float over a tile — Sandbox tab 2. "
                        + "Captures the float MID-ANIMATION → TIMING-VARIANT (not byte-deterministic).",
            OriginalPhase = "25.1-D",
            OriginalScreenshot = "docs/phase-25.1-bottombar.png",
            Width = 940, Height = 1000,
            BuildWindow = () => new MainWindow(),
            AfterShow = w =>
            {
                CaptureRunner.SelectTab(w, 2);
                // Drive the full chain: toolbar command → ReactionPicked → tile float.
                if (w.DataContext is MainWindowViewModel mvm)
                    mvm.Toolbar.SendReactionCommand.Execute("👍");
                Dispatcher.UIThread.RunJobs();
                CaptureRunner.StepAnimation(iterations: 15, sleepMs: 50); // ~750 ms in
            },
        },
    };
}
