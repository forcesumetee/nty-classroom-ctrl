using System.Globalization;
using System.Windows.Data;
using ClassroomCtrl.Teacher.ViewModels;

namespace ClassroomCtrl.Teacher.Converters;

/// <summary>
/// Phase 13-B (Tier 1) — resolves a student endpoint Guid to its current
/// display name by looking it up in the live <see cref="MainViewModel.Students"/>
/// collection.  Used by <c>GroupManagerView</c> to render member chips.
/// Falls back to the abbreviated Guid if the student isn't currently online
/// (e.g. a template just loaded but some members haven't connected yet).
/// </summary>
public class EndpointToDisplayNameConverter : IValueConverter
{
    public object Convert(object? value, System.Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not System.Guid id || id == System.Guid.Empty) return "?";
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            var s = vm.Students.FirstOrDefault(x => x.EndpointId == id);
            if (s != null) return s.DisplayName;
        }
        return id.ToString().Substring(0, 8);
    }

    public object ConvertBack(object? value, System.Type targetType, object? parameter, CultureInfo culture)
        => throw new System.NotImplementedException();
}
