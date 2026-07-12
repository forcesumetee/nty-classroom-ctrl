using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace HeadlessCapture;

/// <summary>
/// Core headless-Skia off-screen capture (no display / screen-recording permission
/// needed). Promoted from the Phase 24.3–25.1 scratchpad harness. All the fiddly
/// bits — Avalonia init, control→window wrapping, layout settling, theme variant,
/// tab selection, and animation-clock stepping — live here so scenarios stay small.
/// </summary>
public static class CaptureRunner
{
    private static bool _inited;

    /// <summary>Idempotent Avalonia bootstrap. UseHeadlessDrawing=false + UseSkia so
    /// frames actually rasterize (headless drawing renders nothing).</summary>
    public static void EnsureInit()
    {
        if (_inited) return;
        AppBuilder.Configure<ClassroomCtrl.Avalonia.Sandbox.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        _inited = true;
    }

    /// <summary>Apply a theme variant ("light"/"dark"); unknown/null leaves it as the
    /// App default (Sandbox App.axaml requests Dark).</summary>
    public static void SetTheme(string? theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme?.ToLowerInvariant() switch
        {
            "light" => ThemeVariant.Light,
            "dark" => ThemeVariant.Dark,
            _ => Application.Current.RequestedThemeVariant,
        };
    }

    /// <summary>Wrap a bare Control in a borderless Window at the given size; pass a
    /// Window through unchanged (just resized).</summary>
    public static Window AsWindow(Control control, int width, int height)
    {
        if (control is Window w)
        {
            w.Width = width;
            w.Height = height;
            return w;
        }
        return new Window
        {
            Width = width,
            Height = height,
            Content = control,
        };
    }

    /// <summary>Show, settle layout, run the optional post-show hook, capture, save PNG.</summary>
    public static void CaptureToFile(Window window, string outPath, Action<Window>? afterShow, int settleIters = 8)
    {
        window.Show();
        Dispatcher.UIThread.RunJobs();
        for (int i = 0; i < settleIters; i++) Dispatcher.UIThread.RunJobs();

        afterShow?.Invoke(window);
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame()
                    ?? throw new InvalidOperationException("CaptureRenderedFrame returned null");
        frame.Save(outPath);
    }

    // ── post-show helpers usable from scenarios ──

    /// <summary>Select a tab by index (first TabControl in the visual tree).</summary>
    public static void SelectTab(Window window, int index)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs != null) tabs.SelectedIndex = index;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Open the first ContextMenu found in the tree (for menu-open baselines).
    /// Avalonia ContextMenu popups DO render into CaptureRenderedFrame.</summary>
    public static void OpenFirstContextMenu(Window window)
    {
        var border = window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.ContextMenu != null);
        border?.ContextMenu?.Open(border);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Open the first ContextMenu, then expand a named submenu item (for
    /// dynamic-submenu baselines, e.g. "Assign to room" → its ItemsSource rooms).</summary>
    public static void OpenContextMenuWithSubmenu(Window window, string submenuHeader)
    {
        var owner = window.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.ContextMenu != null);
        var cm = owner?.ContextMenu;
        if (owner == null || cm == null) return;
        cm.Open(owner);
        Dispatcher.UIThread.RunJobs();
        var mi = cm.Items.OfType<MenuItem>().FirstOrDefault(m => (m.Header as string) == submenuHeader);
        if (mi != null) mi.IsSubMenuOpen = true;
        for (int i = 0; i < 6; i++) Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Advance the headless animation clock ~iterations*sleepMs of REAL time.
    /// Avalonia's headless clock uses wall-clock elapsed time, so we sleep between
    /// forced render ticks. NOTE: this makes animated captures timing-dependent (not
    /// byte-deterministic) — fine for illustration, not for pixel-diff regression.</summary>
    public static void StepAnimation(int iterations = 15, int sleepMs = 50)
    {
        for (int i = 0; i < iterations; i++)
        {
            System.Threading.Thread.Sleep(sleepMs);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
