using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ClassroomCtrl.Avalonia.Teacher.Converters;

/// <summary>
/// TT-2-C — copied from the Sandbox (originally ported from Teacher/Converters).
/// Turns a hex color string ("#RRGGBB" / "#AARRGGBB") into a SolidColorBrush for the
/// tile's presence dot (and future badges). Returns a transparent brush on
/// null/blank/parse failure so a bad value never throws in the binding.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public static readonly HexToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex && !string.IsNullOrWhiteSpace(hex))
        {
            try { return new SolidColorBrush(Color.Parse(hex)); }
            catch { /* fall through */ }
        }
        return Brushes.Transparent;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
