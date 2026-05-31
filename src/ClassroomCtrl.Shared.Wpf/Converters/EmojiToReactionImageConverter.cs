using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace ClassroomCtrl.Shared.Wpf.Converters;

/// <summary>
/// Phase 22.3-C — map the legacy wire-payload emoji glyph string
/// (e.g. "👍" / "❤️" / "😂" / "😮" / "😢") onto the corresponding
/// color reaction PNG shipped in <c>Resources/Reactions/</c>.
///
/// The wire payload is preserved as the existing emoji glyph so this
/// commit is a visual-only swap — peers still receive
/// <see cref="ClassroomCtrl.Shared.Protocol.ReactionMessage.Emoji"/>
/// as the literal glyph and downstream consumers (e.g. recording /
/// history exports) keep working unchanged.  Only the rendering on
/// the picker + the float-up animation switches to PNG.
///
/// Returns null for an unknown / empty input so the bound Image
/// renders nothing (the float-up animation already starts from
/// Opacity=0 — null source = no glyph = clean idle state).
/// </summary>
public class EmojiToReactionImageConverter : IValueConverter
{
    // pack:// URIs are resolved lazily + cached so repeat lookups
    // (every reaction press) are a dictionary hit, not a fresh URI
    // construction + BitmapImage decode.
    private static readonly Dictionary<string, string> EmojiToFileName = new()
    {
        ["👍"] = "thumbs_up.png",
        ["❤"]  = "heart.png",          // U+2764 alone (no VS16)
        ["❤️"] = "heart.png",     // U+2764 + VS16
        ["😂"] = "laugh.png",
        ["😮"] = "surprise.png",
        ["😢"] = "sad.png",
    };

    private static readonly Dictionary<string, BitmapImage> Cache = new();

    private static BitmapImage? Resolve(string emoji)
    {
        if (string.IsNullOrEmpty(emoji)) return null;
        if (Cache.TryGetValue(emoji, out var hit)) return hit;
        if (!EmojiToFileName.TryGetValue(emoji, out var file)) return null;
        try
        {
            var uri = new Uri(
                $"pack://application:,,,/ClassroomCtrl.Shared.Wpf;component/Resources/Reactions/{file}",
                UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            Cache[emoji] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => Resolve(value as string ?? "");

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
