using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
using System.Windows;
using System.Windows.Input;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 8.5: Floating toolbar shown when this student is the breakout-room host.
/// 3 actions, each routed via IPC → Service → TCP to the Teacher (HostActionMessage).
/// </summary>
public partial class HostFloatingToolbar : Window
{
    private readonly Guid _myId;
    private List<(Guid Id, string Name)> _roomPeers = new();

    public HostFloatingToolbar(Guid myEndpointId, string roomName)
    {
        InitializeComponent();
        _myId = myEndpointId;
        RoomNameText.Text = roomName;

        // Default position: lower-right
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Bottom - 240;
    }

    public void UpdateRoomPeers(IEnumerable<(Guid Id, string Name)> peers)
    {
        _roomPeers = peers.Where(p => p.Id != _myId).ToList();
    }

    private void OnDragHandle(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private async void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (_roomPeers.Count == 0)
        {
            System.Windows.MessageBox.Show(Loc.Get("Err_NoRoomPeers"));
            return;
        }
        // Inline picker — quick & dirty, lists peers on click
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var p in _roomPeers)
        {
            var item = new System.Windows.Controls.MenuItem { Header = p.Name };
            var capturedId = p.Id;
            item.Click += async (_, _) => await SendActionAsync(MessageType.HostActionMute, capturedId, "");
            menu.Items.Add(item);
        }
        if (sender is FrameworkElement fe)
        {
            menu.PlacementTarget = fe;
            menu.IsOpen = true;
        }
        await Task.CompletedTask;
    }

    private async void Share_Click(object sender, RoutedEventArgs e)
    {
        await SendActionAsync(MessageType.HostActionShare, null, "");
        // v1 will be denied by teacher; UX-wise we just notify the host.
        System.Windows.MessageBox.Show(Loc.Get("Msg_HostShareDenied"));
    }

    private async void SendToMain_Click(object sender, RoutedEventArgs e)
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            Loc.Get("Lbl_HostMessageHint"),
            Loc.Get("Btn_HostSendToMain"),
            "");
        if (string.IsNullOrWhiteSpace(input)) return;
        await SendActionAsync(MessageType.HostActionMessageToMain, null, input.Trim());
    }

    private async Task SendActionAsync(MessageType type, Guid? targetId, string text)
    {
        if (App.Ipc == null) return;
        var payload = new HostActionMessage
        {
            SenderHostId = _myId,
            TargetStudentId = targetId,
            TextOrPayload = text,
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(payload);
        var env = Envelope.Create(type, bytes, _myId);
        try { await App.Ipc.SendAsync(env); }
        catch (Exception ex) { IpcClient.LogToFile($"[HostToolbar] send {type} failed: {ex.Message}"); }
    }
}
