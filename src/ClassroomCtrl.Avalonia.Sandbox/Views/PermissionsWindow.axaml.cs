using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ClassroomCtrl.Avalonia.Sandbox.Views;

/// <summary>Phase 32-D — first-run permissions onboarding window (shown when Screen Recording isn't
/// granted, and on demand from the tray "Permissions…" item).</summary>
public partial class PermissionsWindow : Window
{
    public PermissionsWindow() => InitializeComponent();

    private void OnContinue(object? sender, RoutedEventArgs e) => Close();
}
