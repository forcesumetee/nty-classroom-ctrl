using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClassroomCtrl.Teacher.Converters;

/// <summary>
/// Phase 3 Section G — visibility router for the main content area.  Visible when
/// value.ToString() equals the parameter (passed as a string).  Used to switch between
/// the student-grid view and the embedded Quiz Manager view without a page navigation.
/// </summary>
public class ViewKindToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var current = value?.ToString() ?? "";
        var target = parameter?.ToString() ?? "";
        return string.Equals(current, target, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
