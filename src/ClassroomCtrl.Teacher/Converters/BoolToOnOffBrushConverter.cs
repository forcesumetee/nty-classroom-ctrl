using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ClassroomCtrl.Teacher.Converters;

/// <summary>
/// Maps a boolean ON/OFF state to a Brush for the floating toolbar.
/// ConverterParameter selects the palette:
///   (none)        Green / Gray (legacy)
///   "Recording"   Red (on) / Green (off)
///   "Subtle"      Accent.PrimarySubtle when on, Transparent when off
///   "Foreground"  Accent.Primary when on, Text.Secondary when off
/// </summary>
public class BoolToOnOffBrushConverter : IValueConverter
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly Brush Gray = new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool on = value is bool b && b;
        var p = parameter as string;

        switch (p)
        {
            case "Recording":
                return on ? Red : Green;
            case "Subtle":
                return on ? ResolveBrush("Accent.PrimarySubtle", Gray) : (Brush)Brushes.Transparent;
            case "Foreground":
                return on ? ResolveBrush("Accent.Primary", Green) : ResolveBrush("Text.Secondary", Gray);
            default:
                return on ? Green : Gray;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;

    private static Brush ResolveBrush(string key, Brush fallback)
    {
        try
        {
            if (Application.Current?.Resources[key] is Brush b) return b;
        }
        catch { }
        return fallback;
    }
}
