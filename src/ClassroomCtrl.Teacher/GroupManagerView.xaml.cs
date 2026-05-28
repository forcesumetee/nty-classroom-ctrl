using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Models;
using ClassroomCtrl.Teacher.Services;
using ClassroomCtrl.Teacher.ViewModels;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using MessageBox = System.Windows.MessageBox;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 13-B (Tier 1) — replaces MultiRoomTeacherView.  Single view for
/// breakout-room create/rename/assign/host/templates.  Reads canonical state
/// from <see cref="App.Server"/> (via MainViewModel.Rooms which mirrors the
/// server via RoomsChanged); writes via the 10 lifecycle methods on
/// ControlServer added in Step 3.
///
/// UX decisions (locked in continuation primer):
///   - Member assignment via CLICK-MENU (chip click → popup "Move to: …").
///   - Destructive ops (Dissolve All, Delete Group, overwrite template) confirm;
///     non-destructive ops (Rename, Reassign, Set Host) do not.
///   - This view shows ONLY the Group Manager; "Join Group" hands off to the
///     separate GroupControllerWindow (Step 6).
/// </summary>
public partial class GroupManagerView : Window
{
    private readonly MainViewModel? _vm;
    private readonly GroupTemplateStore _templateStore = new();

    public GroupManagerView()
    {
        InitializeComponent();
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            _vm = vm;
            RoomsList.ItemsSource = vm.Rooms;
            vm.RoomsCollectionChanged += OnRoomsChangedRefresh;
        }
        Loaded += (_, _) => RefreshUnassigned();
        Closed += (_, _) => { if (_vm != null) _vm.RoomsCollectionChanged -= OnRoomsChangedRefresh; };
    }

    private void OnRoomsChangedRefresh(object? sender, System.EventArgs e)
    {
        // Force re-evaluation of MemberIds.Count bindings on each room row + refresh
        // the Unassigned section.  The Rooms ObservableCollection itself fires its
        // own CollectionChanged when groups are added/removed; this handler is for
        // intra-group member-set changes that don't trigger Rooms collection events.
        Dispatcher.Invoke(RefreshUnassigned);
    }

    /// <summary>Recompute the unassigned-students list = online students not in any group.</summary>
    private void RefreshUnassigned()
    {
        if (_vm == null) return;
        var assigned = new System.Collections.Generic.HashSet<System.Guid>();
        foreach (var r in _vm.Rooms)
            foreach (var id in r.MemberIds) assigned.Add(id);
        var unassigned = _vm.Students.Where(s => !assigned.Contains(s.EndpointId)).ToList();
        UnassignedList.ItemsSource = unassigned;
        UnassignedCountText.Text = unassigned.Count.ToString();
    }

    // ─────── Top-action row ───────

    private async void CreateRandom_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null || _vm == null) return;
        var input = Microsoft.VisualBasic.Interaction.InputBox(
            Loc.Get("Dlg_BreakoutCreateHint"),
            Loc.Get("Dlg_BreakoutCreateTitle"),
            "4");
        if (string.IsNullOrWhiteSpace(input) || !int.TryParse(input.Trim(), out var n) || n < 1 || n > 20) return;

        var students = _vm.Students.Select(s => s.EndpointId).ToList();
        var partitions = BreakoutRoom.AssignRandomly(students, n);
        foreach (var p in partitions)
            await App.Server.CreateGroupAsync(p.Name, p.MemberIds, CancellationToken.None);
        RefreshUnassigned();
    }

    private async void NewEmpty_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null || _vm == null) return;
        var next = _vm.Rooms.Count + 1;
        await App.Server.CreateGroupAsync($"Group {next}", new System.Collections.Generic.List<System.Guid>(), CancellationToken.None);
    }

    private async void AutoBalance_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null || _vm == null) return;
        if (_vm.Rooms.Count == 0)
        {
            MessageBox.Show(Loc.Get("Err_BreakoutNoRooms"));
            return;
        }
        var students = _vm.Students.Select(s => s.EndpointId).OrderBy(_ => System.Random.Shared.Next()).ToList();
        var rooms = _vm.Rooms.ToList();
        for (int i = 0; i < students.Count; i++)
        {
            var room = rooms[i % rooms.Count];
            await App.Server.AssignToRoomAsync(students[i], room.RoomId, room.RoomName, CancellationToken.None);
        }
        RefreshUnassigned();
    }

    private async void DissolveAll_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        if (MessageBox.Show(Loc.Get("GroupMgr_ConfirmDissolveAll"), Loc.Get("GroupMgr_DissolveAll"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await App.Server.DissolveAllAsync(CancellationToken.None);
        RefreshUnassigned();
    }

    // ─────── Per-group buttons ───────

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null || _vm == null) return;
        if (sender is not Button b || b.Tag is not RoomViewModel r) return;
        // Hand off to GroupControllerWindow (Step 6).  Closing the controller
        // emits TeacherLeave.  Re-entry to a different group while joined is
        // handled inside ControlServer.TeacherJoinGroupAsync (auto-leave).
        if (_vm.OpenGroupControllerForRoom is { } open) open(r);
    }

    private async void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        if (sender is not Button b || b.Tag is not RoomViewModel r) return;
        // Step 5 wires the actual screen-share routing.  Step 4 just toggles
        // the server-side flag; ScreenBroadcaster picks it up next frame.
        if (r.IsShareActive)
            await App.Server.StopGroupScreenShareAsync(CancellationToken.None);
        else
            await App.Server.StartGroupScreenShareAsync(r.RoomId, App.SelectedCodec, CancellationToken.None);
    }

    private void GroupMenu_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null || _vm == null) return;
        if (sender is not Button b || b.Tag is not RoomViewModel r) return;

        var menu = new ContextMenu { PlacementTarget = b, IsOpen = true };

        // Rename
        var rename = new MenuItem { Header = Loc.Get("GroupMgr_MenuRename") };
        rename.Click += async (_, _) =>
        {
            var newName = Microsoft.VisualBasic.Interaction.InputBox(
                Loc.Get("GroupMgr_RenamePrompt"), Loc.Get("GroupMgr_MenuRename"), r.RoomName);
            if (string.IsNullOrWhiteSpace(newName) || newName == r.RoomName) return;
            await App.Server.RenameGroupAsync(r.RoomId, newName.Trim(), CancellationToken.None);
        };
        menu.Items.Add(rename);

        // Set Host — submenu of members + "Clear host"
        var setHost = new MenuItem { Header = Loc.Get("GroupMgr_MenuSetHost") };
        var clearHost = new MenuItem { Header = "— (clear)" };
        clearHost.Click += async (_, _) => await App.Server.SetGroupHostAsync(r.RoomId, null, CancellationToken.None);
        setHost.Items.Add(clearHost);
        foreach (var mid in r.MemberIds)
        {
            var capturedId = mid;
            var s = _vm.Students.FirstOrDefault(x => x.EndpointId == capturedId);
            var item = new MenuItem { Header = s?.DisplayName ?? capturedId.ToString().Substring(0, 8) };
            item.Click += async (_, _) => await App.Server.SetGroupHostAsync(r.RoomId, capturedId, CancellationToken.None);
            setHost.Items.Add(item);
        }
        menu.Items.Add(setHost);

        // Delete (confirms)
        var delete = new MenuItem { Header = Loc.Get("GroupMgr_MenuDelete") };
        delete.Click += async (_, _) =>
        {
            if (MessageBox.Show(string.Format(Loc.Get("GroupMgr_ConfirmDeleteGroup"), r.RoomName),
                Loc.Get("GroupMgr_MenuDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;
            await App.Server.DeleteGroupAsync(r.RoomId, CancellationToken.None);
            RefreshUnassigned();
        };
        menu.Items.Add(delete);
    }

    // ─────── Member chip handlers ───────

    /// <summary>Click on a member name → click-menu "Move to: …"  per the
    /// locked-in UX (click-menu, not drag-drop).</summary>
    private void MemberChipName_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null || _vm == null) return;
        if (sender is not Button b || b.Tag is not System.Guid memberId) return;

        var menu = new ContextMenu { PlacementTarget = b, IsOpen = true };
        foreach (var target in _vm.Rooms)
        {
            var capturedRoom = target;
            var item = new MenuItem { Header = capturedRoom.RoomName };
            item.Click += async (_, _) =>
            {
                await App.Server.AssignToRoomAsync(memberId, capturedRoom.RoomId, capturedRoom.RoomName, CancellationToken.None);
                RefreshUnassigned();
            };
            menu.Items.Add(item);
        }
        var unassign = new MenuItem { Header = Loc.Get("GroupMgr_MoveToUnassigned") };
        unassign.Click += async (_, _) =>
        {
            await App.Server.AssignToRoomAsync(memberId, null, "", CancellationToken.None);
            RefreshUnassigned();
        };
        menu.Items.Add(new Separator());
        menu.Items.Add(unassign);
    }

    private async void MemberChipRemove_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        if (sender is not Button b || b.Tag is not System.Guid memberId) return;
        await App.Server.AssignToRoomAsync(memberId, null, "", CancellationToken.None);
        RefreshUnassigned();
    }

    /// <summary>Click on an unassigned student → click-menu "Move to: …" same shape as in-group chip.</summary>
    private void UnassignedChip_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null || _vm == null) return;
        if (sender is not Button b || b.Tag is not System.Guid memberId) return;
        if (_vm.Rooms.Count == 0)
        {
            MessageBox.Show(Loc.Get("Err_BreakoutNoRooms"));
            return;
        }

        var menu = new ContextMenu { PlacementTarget = b, IsOpen = true };
        foreach (var target in _vm.Rooms)
        {
            var capturedRoom = target;
            var item = new MenuItem { Header = capturedRoom.RoomName };
            item.Click += async (_, _) =>
            {
                await App.Server.AssignToRoomAsync(memberId, capturedRoom.RoomId, capturedRoom.RoomName, CancellationToken.None);
                RefreshUnassigned();
            };
            menu.Items.Add(item);
        }
    }

    // ─────── Template store ───────

    private void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var name = Microsoft.VisualBasic.Interaction.InputBox(
            Loc.Get("GroupMgr_SaveTemplatePrompt"), Loc.Get("GroupMgr_SaveTemplate"), "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        if (_templateStore.TemplateExists(name))
        {
            if (MessageBox.Show(string.Format(Loc.Get("GroupMgr_ConfirmOverwriteTemplate"), name),
                Loc.Get("GroupMgr_SaveTemplate"), MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes) return;
        }

        var tmpl = new GroupTemplateStore.Template { Name = name };
        foreach (var room in _vm.Rooms)
        {
            var t = new GroupTemplateStore.TemplateGroup { Name = room.RoomName };
            foreach (var id in room.MemberIds)
            {
                var s = _vm.Students.FirstOrDefault(x => x.EndpointId == id);
                if (s != null) t.MemberDisplayNames.Add(s.DisplayName);
            }
            tmpl.Groups.Add(t);
        }
        _templateStore.UpsertTemplate(tmpl);
        MessageBox.Show(string.Format(Loc.Get("GroupMgr_TemplateSaved"), name));
    }

    private async void LoadTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null || _vm == null) return;
        var store = _templateStore.Load();
        if (store.Templates.Count == 0)
        {
            MessageBox.Show(Loc.Get("GroupMgr_NoTemplatesFound"));
            return;
        }

        var menu = new ContextMenu { PlacementTarget = BtnLoadTemplate, IsOpen = true };
        foreach (var t in store.Templates)
        {
            var captured = t;
            var item = new MenuItem { Header = captured.Name };
            item.Click += async (_, _) =>
            {
                // Apply: dissolve current state first, then create each template group.
                await App.Server.DissolveAllAsync(CancellationToken.None);
                foreach (var g in captured.Groups)
                {
                    // Resolve display names → endpoint Guids; drop unmatched silently.
                    var memberIds = g.MemberDisplayNames
                        .Select(n => _vm.Students.FirstOrDefault(s => string.Equals(s.DisplayName, n, System.StringComparison.OrdinalIgnoreCase)))
                        .Where(s => s != null)
                        .Select(s => s!.EndpointId)
                        .ToList();
                    await App.Server.CreateGroupAsync(g.Name, memberIds, CancellationToken.None);
                }
                RefreshUnassigned();
            };
            menu.Items.Add(item);
        }
        await Task.CompletedTask;
    }
}
