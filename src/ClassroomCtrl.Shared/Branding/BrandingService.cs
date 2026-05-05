using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassroomCtrl.Shared.Branding;

/// <summary>
/// Phase 11.3: Static singleton mirroring the Loc.cs language-dispatch pattern.
/// Each WPF app (Teacher, Student.Agent) calls Initialize() at startup, subscribes
/// to Changed, and on change copies EnumerateResources() entries into
/// Application.Resources so DynamicResource bindings refresh live.
///
/// Resource keys exposed:
///   BrandPrimaryBrush         — main brand color (sidebar, accents)
///   BrandPrimaryDarkBrush     — primary darkened ~15% (sidebar bg)
///   BrandPrimaryLightBrush    — primary lightened ~15% (hover)
///   BrandAccentBrush          — secondary accent
///   BrandAccentDarkBrush
///   BrandLogoSource           — ImageSource (or null when no logo)
///   BrandWallpaperBrush       — Brush for lock screen background
///                               (ImageBrush if WallpaperPath set, else SolidColorBrush)
///   BrandAppName              — string
///   BrandOrgName              — string
///   BrandTitleBar             — string for window titles ("Org — App" or just "App")
/// </summary>
public enum ThemeMode { Light, Dark }

public static class BrandingService
{
    public static BrandingConfig Current { get; private set; } = BrandingConfig.Default();
    public static event Action? Changed;

    public static ThemeMode CurrentTheme =>
        string.Equals(Current.ThemeMode, "Dark", StringComparison.OrdinalIgnoreCase)
            ? ThemeMode.Dark : ThemeMode.Light;

    private static readonly string ConfigFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NTY", "ClassroomCtrl");
    private static readonly string ConfigPath = Path.Combine(ConfigFolder, "branding.json");
    public static readonly string AssetsFolder = Path.Combine(ConfigFolder, "Branding");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static void Initialize()
    {
        try { Directory.CreateDirectory(AssetsFolder); } catch { }
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<BrandingConfig>(json, JsonOpts);
                if (cfg != null) Current = cfg;
            }
        }
        catch { /* fall back to defaults */ }
    }

    public static void Save(BrandingConfig cfg)
    {
        Current = cfg;
        try
        {
            Directory.CreateDirectory(ConfigFolder);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, JsonOpts));
        }
        catch { /* swallow — branding is non-critical */ }
        Changed?.Invoke();
    }

    public static void ResetToDefault() => Save(BrandingConfig.Default());

    /// <summary>
    /// Phase 6C — swap the Colors.* dictionary for the requested theme. Updates
    /// Current.ThemeMode but does NOT persist (call Save afterwards if you want
    /// the choice to survive a restart).
    /// </summary>
    public static void ApplyTheme(ThemeMode mode)
    {
        var app = System.Windows.Application.Current;
        if (app == null) return;

        var newSrc = mode == ThemeMode.Dark
            ? "pack://application:,,,/ClassroomCtrl.Shared;component/Themes/Colors.Dark.xaml"
            : "pack://application:,,,/ClassroomCtrl.Shared;component/Themes/Colors.Light.xaml";

        var newDict = new System.Windows.ResourceDictionary
        {
            Source = new Uri(newSrc, UriKind.Absolute),
        };

        var dicts = app.Resources.MergedDictionaries;
        int found = -1;
        for (int i = 0; i < dicts.Count; i++)
        {
            var s = dicts[i].Source?.ToString() ?? "";
            if (s.Contains("Colors.Light.xaml", StringComparison.OrdinalIgnoreCase)
                || s.Contains("Colors.Dark.xaml", StringComparison.OrdinalIgnoreCase)
                || s.EndsWith("Colors.xaml", StringComparison.OrdinalIgnoreCase))
            {
                found = i;
                break;
            }
        }
        if (found >= 0) dicts[found] = newDict;
        else dicts.Insert(0, newDict);

        Current.ThemeMode = mode == ThemeMode.Dark ? "Dark" : "Light";

        // Re-emit branding overrides on top of the new theme baseline.
        Changed?.Invoke();
    }

    /// <summary>Enumerates all DynamicResource keys for the current branding.</summary>
    public static IEnumerable<KeyValuePair<string, object>> EnumerateResources()
    {
        var primary = ParseColor(Current.PrimaryColor, fallback: Color.FromRgb(0x1E, 0x40, 0xAF));
        var accent = ParseColor(Current.AccentColor, fallback: Color.FromRgb(0x0E, 0xA5, 0xE9));

        yield return Kv("BrandPrimaryBrush", new SolidColorBrush(primary).Frozen());
        yield return Kv("BrandPrimaryDarkBrush", new SolidColorBrush(Shade(primary, -0.20)).Frozen());
        yield return Kv("BrandPrimaryLightBrush", new SolidColorBrush(Shade(primary, +0.18)).Frozen());
        yield return Kv("BrandAccentBrush", new SolidColorBrush(accent).Frozen());
        yield return Kv("BrandAccentDarkBrush", new SolidColorBrush(Shade(accent, -0.18)).Frozen());

        yield return Kv("BrandLogoSource", LoadLogo() ?? (object)System.Windows.DependencyProperty.UnsetValue);

        yield return Kv("BrandWallpaperBrush", BuildWallpaperBrush(primary).Frozen());

        yield return Kv("BrandAppName", Current.AppName);
        yield return Kv("BrandOrgName", Current.OrganizationName);
        yield return Kv("BrandTitleBar",
            Current.ShowOrgInTitle && !string.IsNullOrWhiteSpace(Current.OrganizationName)
                ? $"{Current.OrganizationName} — {Current.AppName}"
                : Current.AppName);

        // Phase 8 (Bug B) — color-role reversal. Customers expected:
        //   * Primary  = theme/skin (banner, sidebar, surfaces) — drives the room "mood"
        //   * Accent   = actionable buttons / focus rings — the click-me color
        // Phase 5C originally had Primary feeding Accent.* keys (i.e. user-Primary became
        // the button color), which inverted that mental model. Swap both axes:
        //   user-Accent  → Accent.* keys           (Button.Primary uses Accent.Primary)
        //   user-Primary → Surface.* tinted keys   (Surface.Background / .Elevated / .Overlay)
        // The Surface.* shade factors here are deeper than typical Material tints so the
        // brand color reads as a *cast* over near-black, not as a flat saturated panel —
        // tested visually on indigo / purple / red / charcoal Primaries.
        yield return Kv("Accent.Primary",        new SolidColorBrush(accent).Frozen());
        yield return Kv("Accent.PrimaryHover",   new SolidColorBrush(Shade(accent, +0.10)).Frozen());
        yield return Kv("Accent.PrimaryPressed", new SolidColorBrush(Shade(accent, -0.20)).Frozen());
        yield return Kv("Accent.PrimarySubtle",  new SolidColorBrush(WithAlpha(accent, 0x33)).Frozen()); // ~20%
        yield return Kv("Surface.BorderFocus",   new SolidColorBrush(accent).Frozen());

        // Theme surfaces tinted by user-Primary. Three tiers mirror Premium Dark
        // Surface.Background / .Elevated / .Overlay so the existing layout reads correctly:
        //   Background ≈ window canvas (deepest)
        //   Elevated   ≈ sidebar / chat panel (one tier up)
        //   Overlay    ≈ hover backgrounds, list-item alt rows
        yield return Kv("Surface.Background", new SolidColorBrush(Shade(primary, -0.85)).Frozen());
        yield return Kv("Surface.Elevated",   new SolidColorBrush(Shade(primary, -0.75)).Frozen());
        yield return Kv("Surface.Overlay",    new SolidColorBrush(Shade(primary, -0.65)).Frozen());
    }

    private static KeyValuePair<string, object> Kv(string k, object v) => new(k, v);

    private static BitmapImage? LoadLogo()
    {
        if (string.IsNullOrWhiteSpace(Current.LogoPath)) return null;
        if (!File.Exists(Current.LogoPath)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(Current.LogoPath, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static Brush BuildWallpaperBrush(Color fallbackPrimary)
    {
        if (!string.IsNullOrWhiteSpace(Current.WallpaperPath) && File.Exists(Current.WallpaperPath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(Current.WallpaperPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch { /* fall through */ }
        }
        return new SolidColorBrush(Shade(fallbackPrimary, -0.40));
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try
        {
            var obj = ColorConverter.ConvertFromString(hex);
            if (obj is Color c) return c;
        }
        catch { }
        return fallback;
    }

    private static Color WithAlpha(Color c, byte a)
        => Color.FromArgb(a, c.R, c.G, c.B);

    /// <summary>Shade a color toward black (factor &lt; 0) or white (factor &gt; 0).</summary>
    private static Color Shade(Color c, double factor)
    {
        factor = Math.Clamp(factor, -1.0, 1.0);
        if (factor < 0)
        {
            double k = 1.0 + factor;
            return Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        }
        return Color.FromRgb(
            (byte)(c.R + (255 - c.R) * factor),
            (byte)(c.G + (255 - c.G) * factor),
            (byte)(c.B + (255 - c.B) * factor));
    }
}

internal static class FreezableExt
{
    public static T Frozen<T>(this T f) where T : System.Windows.Freezable
    {
        if (f.CanFreeze) f.Freeze();
        return f;
    }
}
