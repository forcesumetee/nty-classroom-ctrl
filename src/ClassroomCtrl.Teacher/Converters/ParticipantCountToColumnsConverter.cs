using System;
using System.Globalization;
using System.Windows.Data;

namespace ClassroomCtrl.Teacher.Converters;

/// <summary>
/// Phase 15-C — maps participant count → UniformGrid Columns count per the
/// breakpoint table in <c>docs/conference-mode-ui-mockups.md</c> § 13.
///   1        → 1 col  (single centered tile)
///   2        → 2 cols (1×2 horizontal)
///   3-4      → 2 cols (2×2 grid)
///   5-9      → 3 cols (3×3 grid)
///   10-16    → 4 cols (4×4 grid)
///   17+      → 4 cols (4×4 grid + pagination; only first page renders here)
/// </summary>
public class ParticipantCountToColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        int n = value is int i ? i : 0;
        if (n <= 1) return 1;
        if (n == 2) return 2;
        if (n <= 4) return 2;
        if (n <= 9) return 3;
        return 4;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
