using ClassroomCtrl.Shared.Branding;
using ClassroomCtrl.Shared.Localization;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClassroomCtrl.Teacher.Branding;

public partial class BrandingSettingsWindow : Window
{
    private static readonly string[] PrimaryPalette =
        { "#1E40AF", "#7C3AED", "#0F766E", "#DC2626", "#EA580C", "#1F2937" };

    private static readonly string[] AccentPalette =
        { "#0EA5E9", "#10B981", "#F59E0B", "#EF4444", "#A855F7", "#06B6D4" };

    public BrandingSettingsWindow()
    {
        InitializeComponent();
        LoadIntoFields(BrandingService.Current);
        BuildSwatches(PrimarySwatches, PrimaryPalette, hex => { PrimaryHexBox.Text = hex; });
        BuildSwatches(AccentSwatches, AccentPalette, hex => { AccentHexBox.Text = hex; });
        UpdatePreview();
    }

    private void LoadIntoFields(BrandingConfig cfg)
    {
        AppNameBox.Text = cfg.AppName;
        OrgNameBox.Text = cfg.OrganizationName;
        ShowOrgInTitleBox.IsChecked = cfg.ShowOrgInTitle;
        PrimaryHexBox.Text = cfg.PrimaryColor;
        AccentHexBox.Text = cfg.AccentColor;
        LogoPathBox.Text = cfg.LogoPath;
        WallpaperPathBox.Text = cfg.WallpaperPath;
        // Phase 8 (Bug E) — Theme Mode toggle removed; cfg.ThemeMode stays at "Dark".
    }

    private void BuildSwatches(WrapPanel host, string[] hexes, Action<string> onPick)
    {
        host.Children.Clear();
        foreach (var hex in hexes)
        {
            var btn = new Button
            {
                Width = 28,
                Height = 28,
                Margin = new Thickness(2),
                Background = new SolidColorBrush(ParseColor(hex, Colors.Gray)),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Colors.Gray),
                ToolTip = hex,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btn.Click += (_, _) => onPick(hex);
            host.Children.Add(btn);
        }
    }

    private static Color ParseColor(string hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        try { return (Color)ColorConverter.ConvertFromString(hex)!; } catch { return fallback; }
    }

    private void PrimaryHexBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();
    private void AccentHexBox_TextChanged(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (PreviewBanner == null) return; // before init
        var primary = ParseColor(PrimaryHexBox.Text, Color.FromRgb(0x1E, 0x40, 0xAF));
        var accent = ParseColor(AccentHexBox.Text, Color.FromRgb(0x0E, 0xA5, 0xE9));
        PreviewBanner.Background = new SolidColorBrush(primary);
        PreviewSidebar.Background = new SolidColorBrush(Shade(primary, -0.20));
        // Phase 8 (Bug B): post-reversal, Button.Primary at runtime resolves to user-Accent
        // (BrandingService maps user-Accent → Accent.Primary key). Preview must match —
        // sample the chip with Accent, and pick contrasting text against Accent specifically.
        PreviewBtn.Background = new SolidColorBrush(accent);
        PreviewBtnText.Foreground = new SolidColorBrush(ContrastTextColor(accent));
        PreviewWallpaper.Background = new SolidColorBrush(Shade(primary, -0.40));

        PreviewBannerText.Text = (ShowOrgInTitleBox.IsChecked == true && !string.IsNullOrWhiteSpace(OrgNameBox.Text))
            ? $"{OrgNameBox.Text} — {AppNameBox.Text}"
            : AppNameBox.Text;
        PreviewSideText.Text = AppNameBox.Text;

        if (!string.IsNullOrWhiteSpace(LogoPathBox.Text) && File.Exists(LogoPathBox.Text))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(LogoPathBox.Text, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                PreviewLogo.Source = bmp;
            }
            catch { PreviewLogo.Source = null; }
        }
        else PreviewLogo.Source = null;

        if (!string.IsNullOrWhiteSpace(WallpaperPathBox.Text) && File.Exists(WallpaperPathBox.Text))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(WallpaperPathBox.Text, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                PreviewWallpaper.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            }
            catch { }
        }
    }

    /// <summary>
    /// Picks black or white text for legibility on an arbitrary user-chosen Primary
    /// background. Rec.601 luminance — quick heuristic, sufficient for the small
    /// preview chip; a real WCAG contrast check would also weight against the
    /// dark/light surface but that's overkill for a 96px Sample Button.
    /// </summary>
    private static Color ContrastTextColor(Color bg)
    {
        double luminance = (0.299 * bg.R + 0.587 * bg.G + 0.114 * bg.B) / 255.0;
        return luminance > 0.55 ? Colors.Black : Colors.White;
    }

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

    private void BrowseLogo_Click(object sender, RoutedEventArgs e) => Browse(LogoPathBox);
    private void BrowseWallpaper_Click(object sender, RoutedEventArgs e) => Browse(WallpaperPathBox);
    private void ClearLogo_Click(object sender, RoutedEventArgs e) { LogoPathBox.Text = ""; UpdatePreview(); }
    private void ClearWallpaper_Click(object sender, RoutedEventArgs e) { WallpaperPathBox.Text = ""; UpdatePreview(); }

    private void Browse(TextBox target)
    {
        // Phase 4.2 — Owner pins the picker modally to the Branding window so it
        // doesn't get stranded behind MainWindow when MainWindow steals focus.
        var dlg = new OpenFileDialog
        {
            Title = Loc.Get("Btn_BrowseImage"),
            Filter = "Image files (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
        };
        var result = dlg.ShowDialog(this);
        if (result != true) return;

        // Phase 4.2 — set the user's chosen path FIRST so the field reflects the click
        // even if the AssetsFolder copy fails (no-admin write to %ProgramData%, file
        // already in use by the source app, etc.).  Previously a copy failure left the
        // catch block to set the same path, but if anything threw earlier (Path.Combine
        // on a weird filename, Guid creation) the field stayed empty with no feedback.
        target.Text = dlg.FileName;

        try
        {
            // Best-effort: copy into the branded assets folder so the user can delete
            // their original file.  On success, swap to the copied path.
            Directory.CreateDirectory(BrandingService.AssetsFolder);
            var dest = Path.Combine(BrandingService.AssetsFolder,
                Guid.NewGuid().ToString("N").Substring(0, 8) + Path.GetExtension(dlg.FileName));
            File.Copy(dlg.FileName, dest, overwrite: true);
            target.Text = dest;
        }
        catch
        {
            // Stay on dlg.FileName — already set above.
        }

        UpdatePreview();
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var cfg = new BrandingConfig
        {
            AppName = string.IsNullOrWhiteSpace(AppNameBox.Text) ? "Classroom Control" : AppNameBox.Text.Trim(),
            OrganizationName = OrgNameBox.Text?.Trim() ?? "",
            ShowOrgInTitle = ShowOrgInTitleBox.IsChecked == true,
            PrimaryColor = NormalizeHex(PrimaryHexBox.Text, "#1E40AF"),
            AccentColor = NormalizeHex(AccentHexBox.Text, "#0EA5E9"),
            LogoPath = LogoPathBox.Text ?? "",
            WallpaperPath = WallpaperPathBox.Text ?? "",
            // Phase 4 Section D — Light is now the default theme.  Hardcoded so any legacy
            // branding.json that still says "Dark" gets coerced on next save.
            ThemeMode = "Light",
        };
        // Ensure the Light Colors dictionary is the active baseline before BrandingService
        // overlays the brand-tinted Surface.* / Accent.* keys via Save → Changed.
        BrandingService.ApplyTheme(ClassroomCtrl.Shared.Branding.ThemeMode.Light);
        BrandingService.Save(cfg);  // raises Changed → live re-apply across all windows
        DialogResult = true;
        Close();
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        // Phase 4.2 — BrandingConfig.Default() returns ThemeMode = "Dark" (Phase 6C
        // legacy default).  Plain ResetToDefault() therefore flipped the app back to
        // Dark even after Phase 4 made Light the shipping baseline.  Force Light here
        // so Reset matches the new design language; the rest of BrandingConfig.Default
        // (#1E40AF primary, #0EA5E9 accent, no logo, no wallpaper) stays untouched.
        var defaults = BrandingConfig.Default();
        defaults.ThemeMode = "Light";
        BrandingService.ApplyTheme(ClassroomCtrl.Shared.Branding.ThemeMode.Light);
        BrandingService.Save(defaults);  // raises Changed → live re-apply across windows
        LoadIntoFields(BrandingService.Current);
        UpdatePreview();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string NormalizeHex(string s, string fallback)
    {
        s = s?.Trim() ?? "";
        if (string.IsNullOrEmpty(s)) return fallback;
        if (!s.StartsWith("#")) s = "#" + s;
        try
        {
            ColorConverter.ConvertFromString(s);
            return s;
        }
        catch { return fallback; }
    }
}
