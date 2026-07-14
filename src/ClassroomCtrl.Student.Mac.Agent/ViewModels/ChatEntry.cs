using Avalonia.Layout;
using Avalonia.Media;

namespace ClassroomCtrl.Student.Mac.Agent.ViewModels;

/// <summary>
/// One rendered chat line. Holds the data plus its "modern government" presentation: the student's own
/// messages are navy bubbles aligned right; incoming (Teacher / classmates) are neutral bubbles aligned
/// left. Colours are shared static brushes (no per-message allocation).
/// </summary>
public sealed class ChatEntry
{
    // Restrained, high-contrast palette (shared instances).
    private static readonly IBrush MineBubble = new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x6B));   // navy
    private static readonly IBrush TheirBubble = new SolidColorBrush(Color.FromRgb(0xEE, 0xF1, 0xF5));  // light neutral
    private static readonly IBrush MineText = Brushes.White;
    private static readonly IBrush TheirText = new SolidColorBrush(Color.FromRgb(0x16, 0x18, 0x1D));    // near-black
    private static readonly IBrush MineSubtle = new SolidColorBrush(Color.FromRgb(0xC8, 0xD4, 0xE8));   // pale on navy
    private static readonly IBrush TheirSubtle = new SolidColorBrush(Color.FromRgb(0x5B, 0x61, 0x6B));  // grey on light

    public ChatEntry(string sender, string text, string time, bool isMine)
    {
        Sender = sender;
        Text = text;
        Time = time;
        IsMine = isMine;
    }

    public string Sender { get; }
    public string Text { get; }
    public string Time { get; }
    public bool IsMine { get; }

    public HorizontalAlignment Alignment => IsMine ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public IBrush BubbleBrush => IsMine ? MineBubble : TheirBubble;
    public IBrush TextBrush => IsMine ? MineText : TheirText;
    public IBrush SubtleBrush => IsMine ? MineSubtle : TheirSubtle;
}
