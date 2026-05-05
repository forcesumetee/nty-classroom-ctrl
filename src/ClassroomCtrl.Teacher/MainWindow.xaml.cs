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

    // Phase 3 Section E — track the conversation we're currently subscribed to so the
    // auto-scroll listener can be detached/reattached when the user switches tabs.
    private System.Collections.Specialized.INotifyCollectionChanged? _scrolledMessages;

    private void HookViewModel(ViewModels.MainViewModel? vm)
    {
        if (vm == null) return;
        // Subscribe via INotifyPropertyChanged to track IsScreenSharing transitions.
        // (Avoids leaking subscriptions when DataContext changes — handler is idempotent.)
        vm.PropertyChanged -= VmOnPropertyChanged;
        vm.PropertyChanged += VmOnPropertyChanged;
        UpdateToolbarVisibility(vm.IsScreenSharing, vm);

        // Phase 3 Section E — auto-scroll listens to the active conversation; rebind on switch.
        AttachActiveConversationScrollListener(vm.ActiveConversation);
    }

    private void AttachActiveConversationScrollListener(Shared.Models.Conversation? conv)
    {
        if (_scrolledMessages != null)
            _scrolledMessages.CollectionChanged -= OnChatMessagesChanged;
        _scrolledMessages = conv?.Messages;
        if (_scrolledMessages != null)
            _scrolledMessages.CollectionChanged += OnChatMessagesChanged;
        // Scroll to bottom on tab switch so the user always sees the latest message.
        Dispatcher.BeginInvoke(new Action(() => ChatScrollViewer?.ScrollToEnd()),
            System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnChatMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
        {
            // Defer to dispatcher so the new bubble has been measured before we scroll.
            Dispatcher.BeginInvoke(new Action(() => ChatScrollViewer?.ScrollToEnd()),
                System.Windows.Threading.DispatcherPriority.Render);
        }
    }

    private void VmOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ViewModels.MainViewModel vm) return;
        if (e.PropertyName == nameof(ViewModels.MainViewModel.IsScreenSharing))
        {
            UpdateToolbarVisibility(vm.IsScreenSharing, vm);
        }
        else if (e.PropertyName == nameof(ViewModels.MainViewModel.ActiveConversation))
        {
            // Phase 3 Section E — rebind auto-scroll to the new active conversation.
            AttachActiveConversationScrollListener(vm.ActiveConversation);
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
                vm.AppendSystemChat(Loc.Format("Msg_IPCopied", ip));
        }
        catch (Exception ex)
        {
            // Clipboard can be locked by another app — fail soft.
            if (DataContext is ViewModels.MainViewModel vm)
                vm.AppendErrorChat(ex.Message);
        }
    }

    /// <summary>Phase 3 Section D — toggle the notifications popup; opening also marks
    /// every existing notification read so the badge clears.  History stays in the popup
    /// (and Activity tab) but the unread highlight goes away.</summary>
    private void NotificationsBell_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ViewModels.MainViewModel vm)
            vm.MarkAllNotificationsRead();
        NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;
    }

    // Phase 2 Section D — StudentTile_RightClick moved to StudentCard.xaml.cs (Card_RightClick).
}