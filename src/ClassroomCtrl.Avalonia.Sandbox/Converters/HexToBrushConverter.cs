using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace ClassroomCtrl.Avalonia.Sandbox.Converters;

/// <summary>
/// PORTED from Teacher/Converters/HexToBrushConverter.cs. Turns a hex color string
/// ("#RRGGBB" / "#AARRGGBB") into a SolidColorBrush for badge/dot/REC-button fills.
///
/// WPF→Avalonia: the IValueConverter contract is the same shape; only the namespaces
/// differ (Avalonia.Data.Converters / Avalonia.Media). Avalonia's Color.Parse accepts
/// the same "#..." forms. Returns a transparent brush on null/blank/parse failure so
/// a bad value never throws in the binding.
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
