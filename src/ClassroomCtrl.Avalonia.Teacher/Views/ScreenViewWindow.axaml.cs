using Avalonia.Controls;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;

namespace ClassroomCtrl.Avalonia.Teacher.Views;

/// <summary>
/// TT-3-B — a per-student live screen view. The window owns the stream lifecycle:
/// <c>Opened</c> → <see cref="ScreenViewModel.Start"/> (subscribe + request stream);
/// <c>Closed</c> → <see cref="ScreenViewModel.Stop"/> + Dispose (unsubscribe + stop
/// stream). Routing EVERY teardown through <c>Closed</c> means the stream is stopped
/// no matter how the window goes away — user close, app-quit CloseAll, or
/// disconnect-close — the shipped StudentScreenWindow discipline.
/// </summary>
public partial class ScreenViewWindow : Window
{
    private readonly ScreenViewModel? _vm;

    public ScreenViewWindow() => InitializeComponent();   // XAML/designer ctor

    public ScreenViewWindow(ScreenViewModel vm) : this()
    {
        _vm = vm;
        DataContext = vm;
        Opened += (_, _) => _vm.Start();
        Closed += (_, _) => { _vm.Stop(); _vm.Dispose(); };
    }
}
