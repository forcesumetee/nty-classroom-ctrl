using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Teacher.Services;

namespace ClassroomCtrl.Teacher;

public partial class MainWindow : Window
{
    private BroadcastToolbar? _toolbar;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => HookViewModel(DataContext as ViewModels.MainViewModel);
        Closed += (_, _) => { _toolbar?.Close(); _toolbar = null; };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        HookViewModel(e.NewValue as ViewModels.MainViewModel);
    }

    private void HookViewModel(ViewModels.MainViewModel? vm)
    {
        if (vm == null) return;
        // Subscribe via INotifyPropertyChanged to track IsScreenSharing transitions.
        // (Avoids leaking subscriptions when DataContext changes — handler is idempotent.)
        vm.PropertyChanged -= VmOnPropertyChanged;
        vm.PropertyChanged += VmOnPropertyChanged;
        UpdateToolbarVisibility(vm.IsScreenSharing, vm);
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ViewModels.MainViewModel.IsScreenSharing) && sender is ViewModels.MainViewModel vm)
        {
            UpdateToolbarVisibility(vm.IsScreenSharing, vm);
        }
    }

    private void UpdateToolbarVisibility(bool isBroadcasting, ViewModels.MainViewModel vm)
    {
        if (isBroadcasting)
        {
            if (_toolbar == null)
            {
                // Phase 14.1: NO Owner — toolbar is an independent top-level window so it
                // stays visible when the teacher minimizes MainWindow during a broadcast.
                _toolbar = new BroadcastToolbar { DataContext = vm };
                _toolbar.Closed += (_, _) => _toolbar = null;
                _toolbar.Show();
            }
        }
        else
        {
            _toolbar?.Close();
            _toolbar = null;
        }
    }

    /// <summary>Make sure the floating toolbar dies with the main window.</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        try { _toolbar?.Close(); } catch { }
        _toolbar = null;
        base.OnClosing(e);
    }

    /// <summary>Phase 1.5 — copy the primary local IP to clipboard for deployment teams.</summary>
    private void CopyIP_Click(object sender, RoutedEventArgs e)
    {
        var ip = NetworkInfoService.GetPrimaryIPv4();
        try
        {
            Clipboard.SetText(ip);
            if (DataContext is ViewModels.MainViewModel vm)
                vm.ChatMessages.Add(Loc.Format("Msg_IPCopied", ip));
        }
        catch (Exception ex)
        {
            // Clipboard can be locked by another app — fail soft.
            if (DataContext is ViewModels.MainViewModel vm)
                vm.ChatMessages.Add($"[Error] {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 8 Breakout — remember which student tile was right-clicked,
    /// so AssignToRoomCommand knows the target.
    /// </summary>
    private void StudentTile_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe
            && fe.DataContext is ViewModels.StudentViewModel student
            && DataContext is ViewModels.MainViewModel vm)
        {
            vm.SetPendingAssignStudent(student);
        }
    }
}