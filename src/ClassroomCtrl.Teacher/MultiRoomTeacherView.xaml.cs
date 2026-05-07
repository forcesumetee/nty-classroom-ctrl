using ClassroomCtrl.Teacher.ViewModels;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace ClassroomCtrl.Teacher;

public partial class MultiRoomTeacherView : Window
{
    public MultiRoomTeacherView()
    {
        InitializeComponent();
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            RoomsList.ItemsSource = vm.Rooms;
        }
    }

    private async void SendToRoom_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        if (sender is not Button b || b.Tag is not RoomViewModel r) return;

        var dlg = new MultiRoomMessageInput($"Message for room '{r.RoomName}':") { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Result)) return;

        await App.Server.BroadcastChatToRoomAsync(r.RoomId, dlg.Result, CancellationToken.None);
        MessageBox.Show($"Sent to {r.RoomName}.");
    }
}

internal class MultiRoomMessageInput : Window
{
    private readonly TextBox _box = new()
    {
        Margin = new Thickness(8),
        FontSize = 14,
        Padding = new Thickness(6),
    };
    public string Result => _box.Text;

    public MultiRoomMessageInput(string prompt)
    {
        Title = "Multi-room Message";
        Width = 460;
        Height = 200;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = System.Windows.Media.Brushes.White;
        var stack = new StackPanel { Margin = new Thickness(12) };
        stack.Children.Add(new TextBlock { Text = prompt, FontSize = 13, Margin = new Thickness(4, 4, 4, 8) });
        stack.Children.Add(_box);
        var ok = new Button { Content = "OK", Padding = new Thickness(20, 4, 20, 4), Margin = new Thickness(0, 8, 4, 0), IsDefault = true };
        ok.Click += (_, _) => { DialogResult = true; Close(); };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(20, 4, 20, 4), Margin = new Thickness(4, 8, 0, 0), IsCancel = true };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        btnRow.Children.Add(ok);
        btnRow.Children.Add(cancel);
        stack.Children.Add(btnRow);
        Content = stack;
        _box.Focus();
    }
}
