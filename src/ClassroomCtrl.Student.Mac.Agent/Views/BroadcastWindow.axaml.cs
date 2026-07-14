using Avalonia.Controls;
using Avalonia.Input;

namespace ClassroomCtrl.Student.Mac.Agent.Views;

/// <summary>
/// Fullscreen viewer for the teacher's screen broadcast. Driven by the teacher (Start/Stop) and closed on
/// teacher-disconnect; Esc is a safety valve so a student is never trapped if the stream wedges.
/// </summary>
public partial class BroadcastWindow : Window
{
    public BroadcastWindow()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
