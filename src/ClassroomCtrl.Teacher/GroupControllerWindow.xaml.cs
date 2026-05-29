using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Teacher.ViewModels;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 13-B (Tier 1) — floating controller for the "Join Group" expanded
/// view.  Per the locked-in UX decision, joining a group means opening one
/// <see cref="StudentScreenWindow"/> per group member (tiled) + this small
/// controller window.  The controller is the single source of truth for
/// "teacher is currently joined to group X"; closing it (or [Leave]) closes
/// all opened tiles and emits GroupTeacherLeft.
///
/// Switching to a different group while joined to one: the new Join click
/// goes through ControlServer.TeacherJoinGroupAsync, which auto-emits
/// TeacherLeave for the previous one.  The MainViewModel hook below detects
/// the room change and tells the controller to close its tiles + reopen for
/// the new room.
/// </summary>
public partial class GroupControllerWindow : Window
{
    private readonly RoomViewModel _room;
    private readonly System.Collections.Generic.List<StudentScreenWindow> _tiles = new();
    private bool _leaveInProgress;

    public GroupControllerWindow(RoomViewModel room)
    {
        InitializeComponent();
        _room = room;

        // Default position: top-right of work area.
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top  = area.Top + 24;

        UpdateHeader();
        UpdateShareToggleLabel();
        Loaded += (_, _) => OpenAllTiles();
        Closed += OnClosed;

        // Keep header + share-toggle in sync with server state mutations.
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            vm.RoomsCollectionChanged += OnRoomsChanged;
        }
    }

    private void UpdateHeader()
    {
        HeaderText.Text = string.Format(Loc.Get("GroupCtrl_HeaderFmt"), _room.RoomName);
        SubText.Text = string.Format(Loc.Get("GroupCtrl_MemberCountFmt"), _room.MemberIds.Count);
        // Phase 13-C (Tier 2) Step 5 — chat-location hint.  Sourced from
        // localization so TH/EN both get translated copy.
        ChatHintText.Text = string.Format(Loc.Get("GroupCtrl_ChatHintFmt", "💬 In-group chat appears in the main window prefixed with [{0}]"), _room.RoomName);
    }

    private void UpdateShareToggleLabel()
    {
        ShareToggleText.Text = _room.IsShareActive
            ? Loc.Get("GroupMgr_StopShare")
            : Loc.Get("GroupMgr_Share");
    }

    private void OnRoomsChanged(object? sender, System.EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            // If our room no longer exists OR teacher is now joined to a different
            // one (the GroupManager Join button auto-leaves the prior), close.
            if (System.Windows.Application.Current?.MainWindow?.DataContext is not MainViewModel vm) return;
            var stillExists = vm.Rooms.FirstOrDefault(r => r.RoomId == _room.RoomId);
            if (stillExists == null || !stillExists.IsTeacherJoined)
            {
                Close();
                return;
            }
            UpdateHeader();
            UpdateShareToggleLabel();
        });
    }

    /// <summary>Open one <see cref="StudentScreenWindow"/> per member, tiled in a
    /// staggered cascade across the work-area.  Reuses StudentScreenWindow
    /// unchanged so per-tile Remote Control etc. continue to work (the
    /// "tighter 3-day path" from the design doc).</summary>
    public void OpenAllTiles()
    {
        CloseAllTiles();   // idempotent — closes any orphan tiles from prior calls
        if (System.Windows.Application.Current?.MainWindow?.DataContext is not MainViewModel vm) return;

        var area = SystemParameters.WorkArea;
        int i = 0;
        foreach (var memberId in _room.MemberIds)
        {
            var s = vm.Students.FirstOrDefault(x => x.EndpointId == memberId);
            if (s == null) continue;   // offline group member; skip
            var w = new StudentScreenWindow(memberId, s.DisplayName)
            {
                Owner = System.Windows.Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = area.Left + 20 + (i * 60),
                Top  = area.Top + 20 + (i * 50),
            };
            w.Closed += (_, _) => _tiles.Remove(w);
            w.Show();
            _tiles.Add(w);
            i++;
        }
    }

    private void CloseAllTiles()
    {
        foreach (var w in _tiles.ToList())
        {
            try { w.Close(); } catch { /* ignore */ }
        }
        _tiles.Clear();
    }

    // ─────── Button handlers ───────

    private void OpenAllTiles_Click(object sender, RoutedEventArgs e) => OpenAllTiles();

    private async void ShareToggle_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        var bc = App.ScreenBroadcaster;
        if (bc == null) return;

        if (_room.IsShareActive)
        {
            await App.Server.StopGroupScreenShareAsync(CancellationToken.None);
            bc.Stop();
        }
        else
        {
            if (bc.IsBroadcasting && !bc.TargetGroupId.HasValue)
            {
                MessageBox.Show(Loc.Get("GroupMgr_ShareConflictWholeClass"));
                return;
            }
            bc.Codec = App.SelectedCodec;
            bc.TargetGroupId = _room.RoomId;
            if (!bc.IsBroadcasting) bc.Start();
            await App.Server.StartGroupScreenShareAsync(_room.RoomId, App.SelectedCodec, CancellationToken.None);
        }
    }

    // Phase 13-D step 8 — group-level Mute All / Allow All wire to
    // ControlServer.SendMicMuteRequestAsync per member.  Reliable channel;
    // student-side honors + emits an immediate MicStateUpdate echo.
    private async void MuteAll_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        var reason = Loc.Get("Voice_TeacherMutedYou");
        foreach (var mid in _room.MemberIds)
        {
            await App.Server.SendMicMuteRequestAsync(mid, true, reason, CancellationToken.None);
        }
    }

    private async void AllowAll_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        var reason = Loc.Get("Voice_TeacherUnmutedYou");
        foreach (var mid in _room.MemberIds)
        {
            await App.Server.SendMicMuteRequestAsync(mid, false, reason, CancellationToken.None);
        }
    }

    private async void Leave_Click(object sender, RoutedEventArgs e)
    {
        _leaveInProgress = true;
        if (App.Server != null) await App.Server.TeacherLeaveGroupAsync(CancellationToken.None);
        Close();
    }

    private void OnClosed(object? sender, System.EventArgs e)
    {
        CloseAllTiles();
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel vm)
            vm.RoomsCollectionChanged -= OnRoomsChanged;

        // If the close was driven from outside (Rooms-changed handler detected
        // the teacher is no longer joined OR the room was deleted), don't
        // re-emit TeacherLeave — the trigger already did its job.  If the user
        // clicked the X on this window, _leaveInProgress is still false and we
        // need to emit the Leave so the server state cleans up.
        if (!_leaveInProgress && App.Server != null
            && App.Server.TeacherJoinedGroupId == _room.RoomId)
        {
            _ = App.Server.TeacherLeaveGroupAsync(CancellationToken.None);
        }
    }
}
