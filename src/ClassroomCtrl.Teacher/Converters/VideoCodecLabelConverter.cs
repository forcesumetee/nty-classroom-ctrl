using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
using System;
using System.Globalization;
using System.Windows.Data;

namespace ClassroomCtrl.Teacher.Converters;

/// <summary>Phase 4 Part 4: Render VideoCodec enum as a localized label for ComboBox display.</summary>
public class VideoCodecLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is VideoCodec c)
        {
            return c switch
            {
                VideoCodec.H264 => Loc.Get("Lbl_CodecH264"),
                _               => Loc.Get("Lbl_CodecMjpeg"),
            };
        }
        return value?.ToString() ?? "";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
