using System;
using System.Globalization;
using System.Windows.Data;

namespace ClassroomCtrl.Teacher.Converters;

/// <summary>
/// Phase 3 Section E — MultiBinding helper: returns true if values[0] reference-equals
/// values[1].  Used by the conversation tab strip to highlight the active tab without
/// having to push selection state into each Conversation instance.
/// </summary>
public class AreEqualConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return false;
        return ReferenceEquals(values[0], values[1]);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
