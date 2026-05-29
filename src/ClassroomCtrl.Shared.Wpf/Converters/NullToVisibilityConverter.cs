using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ClassroomCtrl.Shared.Wpf.Converters;

/// <summary>
/// Phase 15-C — null → Collapsed, non-null → Visible.  ConverterParameter
/// "Inverted" flips the mapping (used to show a cam-off placeholder when
/// JpegFrame is null).
///
/// Phase 16-B step 2 — moved from Teacher/Converters/.
/// </summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool isNull = value is null;
        bool inverted = parameter is string s && s.Equals("Inverted", StringComparison.OrdinalIgnoreCase);
        bool show = inverted ? isNull : !isNull;
        return show ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
