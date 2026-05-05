using System.Windows;
using System.Windows.Media;

namespace ClassroomCtrl.Shared.Themes;

/// <summary>
/// Phase 1 — Design system foundation. Helper that lets BrandingService
/// (or any caller) swap the live <c>Accent.Primary</c> / <c>Accent.PrimaryHover</c>
/// brushes in <see cref="Application.Resources"/> at runtime so DynamicResource
/// consumers refresh automatically.
/// </summary>
public static class BrandingOverrides
{
    public static void ApplyAccentColor(Color primary, Color primaryHover)
    {
        if (Application.Current == null) return;
        Application.Current.Resources["Accent.Primary"] = new SolidColorBrush(primary);
        Application.Current.Resources["Accent.PrimaryHover"] = new SolidColorBrush(primaryHover);
    }
}
