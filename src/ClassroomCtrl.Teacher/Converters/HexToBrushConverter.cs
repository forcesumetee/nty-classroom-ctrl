using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace ClassroomCtrl.Teacher.Converters;

/// <summary>Converts a hex color string like "#3B82F6" to a SolidColorBrush.</summary>
public class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try
            {
                return (Brush)new BrushConverter().ConvertFromString(hex)!;
            }
            catch { }
        }
        return Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}