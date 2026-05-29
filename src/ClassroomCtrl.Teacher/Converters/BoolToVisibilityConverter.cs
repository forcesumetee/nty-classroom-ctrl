namespace ClassroomCtrl.Teacher.Converters;

/// <summary>
/// Phase 16-B step 2 — thin re-export shim.  Real implementation lives in
/// <see cref="ClassroomCtrl.Shared.Wpf.Converters.BoolToVisibilityConverter"/>.
/// Existing XAML refs (<c>xmlns:conv="clr-namespace:ClassroomCtrl.Teacher.Converters"</c>)
/// keep working across the 40+ usage sites; a future polish round can
/// sweep them onto the Shared.Wpf namespace and delete this shim.
/// </summary>
public class BoolToVisibilityConverter : ClassroomCtrl.Shared.Wpf.Converters.BoolToVisibilityConverter
{
}
