using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClassroomCtrl.Shared.Wpf.Converters;

/// <summary>
/// True → Visible, False → Collapsed. ConverterParameter "Inverted" flips the mapping.
///
/// Phase 16-B step 2 — moved from Teacher/Converters/.  A thin shim in
/// Teacher.Converters preserves the existing 40+ XAML refs across the
/// shell without a sweeping rename.
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
