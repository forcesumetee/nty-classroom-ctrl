using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ClassroomCtrl.Shared.Models;

namespace ClassroomCtrl.Teacher.Converters;

/// <summary>
/// Phase 3 Section E — DM tabs get a close (✕) button; Everyone tab does not because
/// it can't be reopened from the UI.
/// </summary>
public class ConversationKindToCloseVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is ConversationKind kind && kind == ConversationKind.DM)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
