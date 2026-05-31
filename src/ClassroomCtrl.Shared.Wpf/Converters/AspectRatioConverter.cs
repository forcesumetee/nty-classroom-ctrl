using System;
using System.Globalization;
using System.Windows.Data;

namespace ClassroomCtrl.Shared.Wpf.Converters;

/// <summary>
/// Phase 22.4-A — multiply a width (typically <c>ActualWidth</c> via a
/// <c>RelativeSource Self</c> binding) by an aspect ratio carried in
/// <c>ConverterParameter</c> so the bound height stays in lockstep.
///
/// Used on the Conference tile to lock the OuterBorder at 16:9
/// regardless of the slot the parent UniformGrid hands out — the tile
/// then fills the slot horizontally and computes its own height,
/// instead of being capped at 640x360 and floating with a wide gap on
/// either side (the 22.3-A symptom that the 22.4-A primer captured).
///
/// Parameter is a culture-invariant decimal string (e.g. "0.5625" for
/// 16:9).  Non-double inputs / unparseable parameter → Double.NaN so
/// the bound property keeps whatever default WPF picks up (Auto sizing).
/// </summary>
public class AspectRatioConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double width && !double.IsNaN(width) && width > 0 &&
            parameter is string p &&
            double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out double ratio) &&
            ratio > 0)
        {
            return width * ratio;
        }
        return double.NaN;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
