using MessagePack;

namespace ClassroomCtrl.Shared.Branding;

/// <summary>
/// Phase 11.3: Customer-configurable white-label settings.
/// Persisted as JSON at %ProgramData%\NTY\ClassroomCtrl\branding.json so both
/// Teacher and Student.Agent (which run as different users / processes) read
/// the same file. Color values are 7-char hex (#RRGGBB).
/// </summary>
[MessagePackObject(true)]
public class BrandingConfig
{
    public string PrimaryColor { get; set; } = "#1E40AF";
    public string AccentColor { get; set; } = "#0EA5E9";
    public string LogoPath { get; set; } = "";
    public string WallpaperPath { get; set; } = "";
    public string AppName { get; set; } = "Classroom Control";
    public string OrganizationName { get; set; } = "NTY MULTIMEDIA";
    public bool ShowOrgInTitle { get; set; } = true;
    public bool ShowLogoInHeader { get; set; } = true;

    // Phase 6C — Light/Dark toggle. Stored as string ("Light"/"Dark") so the JSON
    // survives an enum rename.
    // Default = Dark: a fresh Reset-to-default produced an almost-all-white UI on Light,
    // which Lead flagged as broken. Premium Dark is the original brand experience.
    public string ThemeMode { get; set; } = "Dark";

    public static BrandingConfig Default() => new();
}
