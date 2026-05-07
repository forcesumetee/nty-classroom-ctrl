using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClassroomCtrl.Teacher.Converters;

/// <summary>
/// True → Visible, False → Collapsed. ConverterParameter "Inverted" flips the mapping.
/// </summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool on = value is bool b && b;
        bool inverted = parameter is string s && s.Equals("Inverted", StringComparison.OrdinalIgnoreCase);
        if (inverted) on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
