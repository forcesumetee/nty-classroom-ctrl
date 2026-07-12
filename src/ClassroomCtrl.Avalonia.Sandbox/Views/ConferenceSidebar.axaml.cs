using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassroomCtrl.Avalonia.Sandbox.ViewModels;

namespace ClassroomCtrl.Avalonia.Sandbox.Views;

/// <summary>
/// PORTED from Shared.Wpf/Conference/ConferenceSidebar.xaml.cs (Phase 25.3).
///
/// Dropped from the WPF version (Windows-only / deferred per Phase 25.3 scope):
///   • The attach-picker / Download / Open reflection bridges + SaveFileDialog
///     (Microsoft.Win32) — the attachment card renders but its buttons are inert.
///   • The IConferenceSidebarHost cast lives in Shared.Wpf; here the close button
///     just flips the demo VM's IsConferenceSidebarVisible directly.
/// </summary>
public partial class ConferenceSidebar : UserControl
{
    public ConferenceSidebar()
    {
        InitializeComponent();
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConferenceSidebarDemoViewModel vm)
            vm.IsConferenceSidebarVisible = false;
    }
}
