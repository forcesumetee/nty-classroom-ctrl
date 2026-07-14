using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using ClassroomCtrl.Student.Mac.Agent.ViewModels;

namespace ClassroomCtrl.Student.Mac.Agent.Views;

/// <summary>
/// The class-chat utility window. A standard resizable/minimizable frame (not modal, not topmost) so it
/// never blocks the screen. Auto-scrolls to the newest message as history grows.
/// </summary>
public partial class ChatWindow : Window
{
    public ChatWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (DataContext is ChatViewModel vm)
            vm.Messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        // Marshal + defer so layout has placed the new bubble before we scroll to it.
        Dispatcher.UIThread.Post(() => Scroll.ScrollToEnd(), DispatcherPriority.Background);
    }
}
