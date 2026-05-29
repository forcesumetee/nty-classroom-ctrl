using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Student.Agent.Dialogs;
using ClassroomCtrl.Student.Agent.Models;
using ClassroomCtrl.Student.Agent.Setup;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;

namespace ClassroomCtrl.Student.Agent;

public partial class MainWindow : Window
{
    private bool _handRaised;
    private LockOverlayWindow? _lockOverlay;

    // Phase 8: Breakout Room state
    private System.Guid? _myRoomId;
    private string _myRoomName = "";

    // Phase 4 Part 1: Screen sharing viewer
    private ScreenViewWindow? _screenViewWindow;

    // Phase 4 Part 2: Outgoing stream when teacher requests "View Full Screen"
    private StudentBroadcaster? _studentBroadcaster;

    // Phase 4 Part 3a: Teacher audio playback
    private AudioPlayer? _audioPlayer;

    // Phase 4 Part 3b: Student talkback mic
    private StudentAudioBroadcaster? _studentAudio;
    private bool _micOn;

    // Phase 13: Lockdown exam window
    private LockdownExamWindow? _examWindow;

    // Phase 8.5: Host state — set when BreakoutAssign arrives with HostStudentId == me
    private HostFloatingToolbar? _hostToolbar;
    private Guid? _myEndpointId;

    // Phase 9.1: Demo state — when peer is demonstrating, this window shows their screen.
    private PeerScreenViewWindow? _demoPeerWindow;
    private Guid? _demoSourceId;

    // Phase 13-C (Tier 2): group-presenter viewer — opened when the in-group
    // host starts presenting; closed on host change, host clear, or this
    // student being moved to a different room.  Host's own Agent never opens
    // it (sender-side self-filter on env.SenderId == _myEndpointId).
    private GroupPeerView? _groupPeerView;

    // Phase 9.5: Camera view + Phase 9.6: Movie player + Phase 6.5: Remote banner
    private CameraViewWindow? _cameraWindow;
    private MoviePlayerWindow? _movieWindow;
    private RemoteControlBanner? _remoteBanner;

    // Phase 9 Section B / Phase 9.1 Section B+C — Bell + Notifications.
    // The bell tracks unread NOTIFICATIONS (system events), not chat — chat
    // and system events are now separated: chat lives in the body ChatList,
    // system events route through Notifications and surface via the popup
    // anchored to the bell.  Unread counter only bumps when the window is
    // hidden / unfocused (so users actively reading don't see a counter for
    // events they just saw); resets on bell click + on window activate.
    public ObservableCollection<StudentNotification> Notifications { get; } = new();
    private int _unreadBellCount;

    public MainWindow()
    {
        InitializeComponent();
        Hide();

        IpcClient.LogToFile($"[MainWindow] ctor; App.Ipc null? {App.Ipc == null}");

        if (App.Ipc != null)
        {
            App.Ipc.MessageReceived += OnIpcMessage;
            IpcClient.LogToFile("[MainWindow] Subscribed to App.Ipc.MessageReceived");
        }

        Loc.LanguageChanged += UpdateMicButtonText;
        UpdateMicButtonText();

        // Phase 9.1 Section B — bind ItemsControl to Notifications, watch for
        // additions to bump the bell badge.  Empty-state placeholder visibility
        // also tracks the collection so the popup never shows a blank scroller.
        NotificationsItems.ItemsSource = Notifications;
        Notifications.CollectionChanged += OnNotificationsChanged;
        Activated += (_, _) => ResetBellBadge();
        UpdateNoNotificationsPlaceholder();
    }

    /// <summary>Phase 9.1 Section C — single entry point for system events.
    /// Routes to the bell-popup Notifications list instead of the body chat.
    /// Newest-first (Insert at 0); caps the list at 50 to avoid unbounded
    /// growth across long sessions.</summary>
    private void AddSystemNotification(string message, string icon = "ℹ️")
    {
        Dispatcher.Invoke(() =>
        {
            Notifications.Insert(0, new StudentNotification
            {
                Message = message,
                Icon = icon,
                Timestamp = System.DateTime.Now,
            });
            while (Notifications.Count > 50) Notifications.RemoveAt(Notifications.Count - 1);
        });
    }

    /// <summary>Phase 10.13.1 — Windows toast when chat arrives but MainWindow is hidden in tray
    /// or minimized.  Uses the existing TrayIconManager.ShowBalloon (H.NotifyIcon-based);
    /// Windows 10/11 surfaces it as a modern toast with the default notification sound.
    /// DND toggle, custom overlay UI, reply-from-popup all deferred to Phase 10.14.
    /// Caller is responsible for invoking on the UI thread (IsVisible/WindowState are
    /// dispatcher-affined) — every call site is already inside Dispatcher.Invoke.</summary>
    private void ShowChatToastIfHidden(MessageType kind, string body)
    {
        // Phase 10.14 (Item 5) — added IsActive so we also toast when the window is
        // open in the background and another app has focus. The customer reported
        // "I was using Word and missed a chat" after 10.13.1 shipped.
        if (IsVisible && IsActive && WindowState != WindowState.Minimized) return;

        // Phase 10.14 (Item 8) — was hard-coded Thai in 10.13.1; now Loc-resolved
        // so the toast title respects the Teacher's selected language across all 10
        // supported locales.  See LocalizationData.cs Toast_Chat* keys.
        var title = kind switch
        {
            MessageType.ChatBroadcast => Loc.Get("Toast_ChatBroadcast"),
            MessageType.ChatDirect    => Loc.Get("Toast_ChatDirect"),
            MessageType.ChatRoom      => Loc.Get("Toast_ChatRoom"),
            _ => Loc.Get("Toast_ChatBroadcast"),
        };
        // Truncate so the toast doesn't get clipped mid-sentence by Windows;
        // 120 chars leaves room for the title + ellipsis on a one-line toast.
        var trimmed = body ?? "";
        if (trimmed.Length > 120) trimmed = trimmed.Substring(0, 117) + "...";
        App.Tray?.ShowBalloon(title, trimmed);
    }

    private void OnNotificationsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        UpdateNoNotificationsPlaceholder();
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) return;
        // Don't bump the bell while the user is actively looking at the window.
        if (IsActive && IsVisible) return;
        _unreadBellCount += e.NewItems?.Count ?? 0;
        UpdateBellBadge();
    }

    private void UpdateNoNotificationsPlaceholder()
    {
        Dispatcher.Invoke(() =>
        {
            NoNotificationsText.Visibility =
                Notifications.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        });
    }

    private void UpdateBellBadge()
    {
        Dispatcher.Invoke(() =>
        {
            if (_unreadBellCount <= 0)
            {
                BellBadge.Visibility = Visibility.Collapsed;
                return;
            }
            BellBadgeText.Text = _unreadBellCount > 99 ? "99+" : _unreadBellCount.ToString();
            BellBadge.Visibility = Visibility.Visible;
        });
    }

    private void ResetBellBadge()
    {
        if (_unreadBellCount == 0) return;
        _unreadBellCount = 0;
        UpdateBellBadge();
    }

    private void Bell_Click(object sender, RoutedEventArgs e)
    {
        // Phase 9.1 Section B — toggle popup + clear unread.  StaysOpen=False
        // on the Popup also handles outside-clicks, so manual close-on-second-
        // click is purely a convenience.
        NotificationsPopup.IsOpen = !NotificationsPopup.IsOpen;
        ResetBellBadge();
    }

    private void UpdateMicButtonText()
    {
        Dispatcher.Invoke(() =>
        {
            MicButton.Content = Loc.Get(_micOn ? "Btn_StudentMicOff" : "Btn_StudentMicOn");
        });
    }

    private void OnIpcMessage(object? sender, Envelope env)
    {
        IpcClient.LogToFile($"[MainWindow] OnIpcMessage type={env.Type}");

        // Phase 8.5: Learn our own endpoint id from any targeted envelope.
        // Service-side IsForMe filter guarantees env.TargetEndpointId is either Empty (broadcast)
        // or our own id when forwarded down. Once captured, _myEndpointId is stable.
        if (_myEndpointId == null && env.TargetEndpointId != Guid.Empty)
        {
            _myEndpointId = env.TargetEndpointId;
            IpcClient.LogToFile($"[MainWindow] Captured my endpoint id: {_myEndpointId}");
        }

        switch (env.Type)
        {
            case MessageType.ChatBroadcast:
                {
                    var chat = MessagePack.MessagePackSerializer.Deserialize<ChatMessage>(env.Payload);
                    Dispatcher.Invoke(() =>
                    {
                        ChatList.Items.Add($"[{chat.SenderName}] {chat.Text}");
                        ShowChatToastIfHidden(MessageType.ChatBroadcast, chat.Text);
                    });
                }
                break;

            case MessageType.ChatDirect:
                {
                    var chat = MessagePack.MessagePackSerializer.Deserialize<ChatMessage>(env.Payload);
                    Dispatcher.Invoke(() =>
                    {
                        ChatList.Items.Add($"[DM from {chat.SenderName}] {chat.Text}");
                        ShowChatToastIfHidden(MessageType.ChatDirect, chat.Text);
                    });
                }
                break;

            case MessageType.ChatRoom:
                {
                    var chat = MessagePack.MessagePackSerializer.Deserialize<ChatMessage>(env.Payload);
                    Dispatcher.Invoke(() =>
                    {
                        ChatList.Items.Add($"[Room] [{chat.SenderName}] {chat.Text}");
                        ShowChatToastIfHidden(MessageType.ChatRoom, chat.Text);
                    });
                }
                break;

            case MessageType.BreakoutAssign:
                {
                    var assign = MessagePack.MessagePackSerializer.Deserialize<BreakoutAssignMessage>(env.Payload);
                    var newRoom = assign.RoomId == System.Guid.Empty ? (System.Guid?)null : assign.RoomId;
                    _myRoomId = newRoom;
                    _myRoomName = assign.RoomName;

                    // Phase 8.5: am I the host?
                    var iAmHost = assign.HostStudentId.HasValue
                                  && _myEndpointId.HasValue
                                  && assign.HostStudentId.Value == _myEndpointId.Value;

                    AddSystemNotification(
                        newRoom.HasValue
                            ? Loc.Format("Chat_MovedToRoom", assign.RoomName)
                            : Loc.Get("Chat_ReturnedToMain"),
                        "👥");
                    Dispatcher.Invoke(() =>
                    {
                        if (iAmHost)
                        {
                            if (_hostToolbar == null)
                            {
                                _hostToolbar = new HostFloatingToolbar(_myEndpointId!.Value, assign.RoomName);
                                _hostToolbar.Show();
                                App.Tray?.ShowBalloon(Loc.Get("Lbl_AppName"),
                                    Loc.Format("Toast_YouAreHost", assign.RoomName));
                                AddSystemNotification(Loc.Format("Toast_YouAreHost", assign.RoomName), "👑");
                            }
                        }
                        else
                        {
                            if (_hostToolbar != null)
                            {
                                _hostToolbar.Close();
                                _hostToolbar = null;
                            }
                        }

                        // Phase 13-C (Tier 2) — if my room changed, any open
                        // GroupPeerView is for the wrong group's presenter.
                        // Close it; a new Start envelope (relayed for the new
                        // group's host) will reopen if applicable.
                        if (_groupPeerView != null && _groupPeerView.GroupId != newRoom.GetValueOrDefault())
                        {
                            _groupPeerView.Close();
                            _groupPeerView = null;
                        }
                        // Defense-in-depth: if I was the host of my old room
                        // and the teacher reassigned me without first clearing
                        // my host role, my broadcaster is still fanning out
                        // to a group I'm no longer in.  Stop it.  Normal flow
                        // sends Stop before reassign, but this catches the
                        // out-of-order case.
                        if (_studentBroadcaster?.GroupBroadcastGroupId is Guid gid && gid != newRoom.GetValueOrDefault())
                        {
                            StopGroupHostBroadcast();
                        }
                    });
                }
                break;

            case MessageType.StudentRecordingNotify:
                try
                {
                    var n = MessagePack.MessagePackSerializer.Deserialize<StudentRecordingNotifyMessage>(env.Payload);
                    Dispatcher.Invoke(() =>
                    {
                        if (n.IsRecording)
                            App.Tray?.ShowBalloon(Loc.Get("Lbl_AppName"),
                                Loc.Get("Toast_TeacherRecording"));
                    });
                }
                catch (Exception ex) { IpcClient.LogToFile($"[MainWindow] StudentRecordingNotify decode: {ex.Message}"); }
                break;

            // Phase 6.6: PDPA balloon — teacher captured my screen
            case MessageType.StudentScreenshotNotify:
                Dispatcher.Invoke(() =>
                {
                    App.Tray?.ShowBalloon(Loc.Get("Lbl_AppName"), Loc.Get("Toast_TeacherScreenshot"));
                });
                break;

            // Phase 6.6: One-shot full-resolution screenshot for teacher's request.
            case MessageType.RequestScreenshot:
                _ = HandleScreenshotRequestAsync(env);
                break;

            case MessageType.LockScreen:
                Dispatcher.Invoke(() =>
                {
                    if (_lockOverlay == null)
                    {
                        _lockOverlay = new LockOverlayWindow();
                        _lockOverlay.Show();
                        IpcClient.LogToFile("[MainWindow] Lock overlay shown");
                    }
                });
                break;

            case MessageType.UnlockScreen:
                Dispatcher.Invoke(() =>
                {
                    if (_lockOverlay != null)
                    {
                        _lockOverlay.Close();
                        _lockOverlay = null;
                        IpcClient.LogToFile("[MainWindow] Lock overlay closed");
                    }
                });
                break;

            // ─────── Phase 4 Part 1: Screen Sharing ───────

            case MessageType.ScreenStreamStart:
            // Phase 13-B (Tier 1) — group-targeted teacher screen share routes
            // through the same render path as whole-class; only the routing
            // differs (Service-side IsForMe filters by TargetGroupId so out-of-
            // group students never see these envelopes here).
            case MessageType.GroupScreenStreamStart:
                IpcClient.LogToFile($"[MainWindow] Screen stream START ({env.Type})");
                Dispatcher.Invoke(EnsureScreenViewWindow);
                break;

            case MessageType.ScreenStreamFrame:
            case MessageType.GroupScreenStreamFrame:
                {
                    var frame = MessagePack.MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(env.Payload);
                    Dispatcher.Invoke(() =>
                    {
                        EnsureScreenViewWindow();
                        _screenViewWindow?.UpdateFrame(
                            frame.FrameData, frame.Width, frame.Height,
                            frame.TimestampUtcMs, frame.FrameSeq,
                            frame.Codec, frame.IsKeyframe);
                    });
                }
                break;

            case MessageType.ScreenStreamStop:
            case MessageType.GroupScreenStreamStop:
                IpcClient.LogToFile($"[MainWindow] Screen stream STOP ({env.Type})");
                Dispatcher.Invoke(() =>
                {
                    _screenViewWindow?.Close();
                    _screenViewWindow = null;
                });
                break;

            // ─────── Phase 13-C (Tier 2): in-group peer presenter ───────
            // Teacher emits group-targeted Start/Stop envelopes via
            // SetGroupHostAsync.  The host receives via TargetGroupId loopback
            // and self-detects from payload.PresenterId == own EndpointId;
            // the host's broadcaster starts/stops accordingly.  Non-presenter
            // peers open/close GroupPeerView based on the same payload field.
            // Frames are sent by the host's broadcaster directly (env.SenderId
            // = host); the existing self-filter on env.SenderId == self
            // handles the host's frame loopback.
            case MessageType.StudentGroupScreenStreamStart:
                try
                {
                    var ctrl = MessagePack.MessagePackSerializer.Deserialize<StudentGroupScreenStreamControlMessage>(env.Payload);
                    bool iAmPresenter = _myEndpointId.HasValue && ctrl.PresenterId == _myEndpointId.Value;
                    IpcClient.LogToFile($"[MainWindow] StudentGroupScreenStreamStart presenter={ctrl.PresenterName} group={ctrl.GroupId} codec={ctrl.Codec} iAmPresenter={iAmPresenter}");
                    Dispatcher.Invoke(() =>
                    {
                        if (iAmPresenter)
                        {
                            // Close any GroupPeerView I had open as a non-host
                            // viewer (covers the host-rotation A→C→A case).
                            _groupPeerView?.Close();
                            _groupPeerView = null;
                            StartGroupHostBroadcast(ctrl.GroupId, ctrl.Codec);
                            return;
                        }
                        // Host changed under us — close prior viewer so the
                        // new presenter opens fresh instead of mixing frames.
                        if (_groupPeerView != null && _groupPeerView.PresenterId != ctrl.PresenterId)
                        {
                            _groupPeerView.Close();
                            _groupPeerView = null;
                        }
                        if (_groupPeerView == null)
                        {
                            _groupPeerView = new GroupPeerView(ctrl.PresenterId, ctrl.PresenterName, ctrl.GroupId, _myRoomName ?? "");
                            _groupPeerView.Closed += (_, _) => _groupPeerView = null;
                            _groupPeerView.Show();
                        }
                    });
                }
                catch (Exception ex) { IpcClient.LogToFile($"[MainWindow] StudentGroupScreenStreamStart decode: {ex.Message}"); }
                break;

            case MessageType.StudentGroupScreenStreamFrame:
                if (_myEndpointId.HasValue && env.SenderId == _myEndpointId.Value) break;
                try
                {
                    var frame = MessagePack.MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(env.Payload);
                    var presenterId = env.SenderId;
                    Dispatcher.Invoke(() =>
                    {
                        // Defensive: open viewer on first frame if we missed
                        // the Start envelope (teacher restart / late join).
                        // Labeling will be corrected by the next Start.
                        if (_groupPeerView == null && env.TargetGroupId.HasValue)
                        {
                            _groupPeerView = new GroupPeerView(presenterId, "", env.TargetGroupId.Value, _myRoomName ?? "");
                            _groupPeerView.Closed += (_, _) => _groupPeerView = null;
                            _groupPeerView.Show();
                        }
                        _groupPeerView?.UpdateFrame(
                            frame.FrameData, frame.Width, frame.Height,
                            frame.TimestampUtcMs, frame.FrameSeq,
                            frame.Codec, frame.IsKeyframe);
                    });
                }
                catch { /* drop bad frames quietly */ }
                break;

            case MessageType.StudentGroupScreenStreamStop:
                try
                {
                    var ctrl = MessagePack.MessagePackSerializer.Deserialize<StudentGroupScreenStreamControlMessage>(env.Payload);
                    bool iWasPresenter = _myEndpointId.HasValue && ctrl.PresenterId == _myEndpointId.Value;
                    IpcClient.LogToFile($"[MainWindow] StudentGroupScreenStreamStop presenter={ctrl.PresenterId} iWasPresenter={iWasPresenter}");
                    Dispatcher.Invoke(() =>
                    {
                        if (iWasPresenter)
                        {
                            StopGroupHostBroadcast();
                            return;
                        }
                        _groupPeerView?.Close();
                        _groupPeerView = null;
                    });
                }
                catch (Exception ex) { IpcClient.LogToFile($"[MainWindow] StudentGroupScreenStreamStop decode: {ex.Message}"); }
                break;

            // ─────── Phase 9.1: Student Demonstration ───────

            case MessageType.DemoStart:
                try
                {
                    var ds = MessagePack.MessagePackSerializer.Deserialize<DemoStartMessage>(env.Payload);
                    _demoSourceId = ds.SourceStudentId;
                    Dispatcher.Invoke(() =>
                    {
                        // Source student does NOT open peer window — their own screen is captured
                        // by the StudentBroadcaster via the StudentStreamStart message that follows.
                        if (_myEndpointId.HasValue && _myEndpointId.Value == ds.SourceStudentId)
                        {
                            App.Tray?.ShowBalloon(Loc.Get("Lbl_AppName"), Loc.Get("Toast_YouAreDemoing"));
                            AddSystemNotification(Loc.Get("Toast_YouAreDemoing"), "🎤");
                            return;
                        }
                        if (_demoPeerWindow == null)
                        {
                            _demoPeerWindow = new PeerScreenViewWindow(ds.SourceName);
                            _demoPeerWindow.Closed += (_, _) => _demoPeerWindow = null;
                            _demoPeerWindow.Show();
                            AddSystemNotification(Loc.Format("Lbl_DemoActive", ds.SourceName), "🎤");
                        }
                    });
                }
                catch (Exception ex) { IpcClient.LogToFile($"[MainWindow] DemoStart decode: {ex.Message}"); }
                break;

            case MessageType.DemoFrame:
                try
                {
                    var frame = MessagePack.MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(env.Payload);
                    Dispatcher.Invoke(() =>
                    {
                        _demoPeerWindow?.UpdateFrame(
                            frame.FrameData, frame.Width, frame.Height,
                            frame.TimestampUtcMs, frame.FrameSeq,
                            frame.Codec, frame.IsKeyframe);
                    });
                }
                catch { }
                break;

            case MessageType.DemoStop:
                _demoSourceId = null;
                Dispatcher.Invoke(() =>
                {
                    _demoPeerWindow?.Close();
                    _demoPeerWindow = null;
                });
                break;

            // ─────── Phase 9.2: Screen Pen overlay strokes ───────

            case MessageType.DrawingStroke:
                try
                {
                    var stroke = MessagePack.MessagePackSerializer.Deserialize<DrawingStrokeMessage>(env.Payload);
                    Dispatcher.Invoke(() => _screenViewWindow?.AddStroke(stroke));
                }
                catch { }
                break;

            case MessageType.DrawingClear:
                Dispatcher.Invoke(() => _screenViewWindow?.ClearStrokes());
                break;

            case MessageType.DrawingUndo:
                Dispatcher.Invoke(() => _screenViewWindow?.UndoStroke());
                break;

            // ─────── Phase 9.5: Camera Broadcast ───────

            case MessageType.CameraStart:
                Dispatcher.Invoke(() =>
                {
                    if (_cameraWindow == null)
                    {
                        _cameraWindow = new CameraViewWindow();
                        _cameraWindow.Closed += (_, _) => _cameraWindow = null;
                        _cameraWindow.Show();
                    }
                });
                break;

            case MessageType.CameraFrame:
                try
                {
                    var cf = MessagePack.MessagePackSerializer.Deserialize<CameraFrameMessage>(env.Payload);
                    Dispatcher.Invoke(() => _cameraWindow?.UpdateFrame(cf.JpegData));
                }
                catch { }
                break;

            case MessageType.CameraStop:
                Dispatcher.Invoke(() =>
                {
                    _cameraWindow?.Close();
                    _cameraWindow = null;
                });
                break;

            // ─────── Phase 9.6: Net Movie ───────

            case MessageType.MoviePlay:
                try
                {
                    var mp = MessagePack.MessagePackSerializer.Deserialize<MoviePlayMessage>(env.Payload);
                    // Phase 10.20 — was silent catch; now logged so future
                    // Net Movie failures show up in agent-debug.log.
                    // Phase 10.21 — log expected size too so a "file truncated"
                    // regression is diagnosable from one log line.
                    IpcClient.LogToFile($"[MainWindow] MoviePlay received: file='{mp.FileName}' seek={mp.SeekTime} playAtMs={mp.PlayAtTimestampMs} expectedSize={mp.ExpectedFileSizeBytes}");
                    Dispatcher.Invoke(() =>
                    {
                        if (_movieWindow == null)
                        {
                            _movieWindow = new MoviePlayerWindow();
                            _movieWindow.Closed += (_, _) => _movieWindow = null;
                            _movieWindow.Show();
                        }
                        _movieWindow.Play(mp.FileName, mp.SeekTime, mp.PlayAtTimestampMs, mp.ExpectedFileSizeBytes);
                    });
                }
                catch (Exception ex)
                {
                    IpcClient.LogToFile($"[MainWindow] MoviePlay handler threw: {ex.GetType().Name}: {ex.Message}");
                }
                break;

            case MessageType.MoviePause:
                try
                {
                    var ms = MessagePack.MessagePackSerializer.Deserialize<MovieSeekMessage>(env.Payload);
                    IpcClient.LogToFile($"[MainWindow] MoviePause received: seek={ms.SeekTime}");
                    Dispatcher.Invoke(() => _movieWindow?.Pause(ms.SeekTime));
                }
                catch (Exception ex)
                {
                    IpcClient.LogToFile($"[MainWindow] MoviePause handler threw: {ex.GetType().Name}: {ex.Message}");
                }
                break;

            case MessageType.MovieSeek:
                try
                {
                    var ms = MessagePack.MessagePackSerializer.Deserialize<MovieSeekMessage>(env.Payload);
                    IpcClient.LogToFile($"[MainWindow] MovieSeek received: seek={ms.SeekTime}");
                    Dispatcher.Invoke(() => _movieWindow?.Seek(ms.SeekTime));
                }
                catch (Exception ex)
                {
                    IpcClient.LogToFile($"[MainWindow] MovieSeek handler threw: {ex.GetType().Name}: {ex.Message}");
                }
                break;

            case MessageType.MovieStop:
                IpcClient.LogToFile("[MainWindow] MovieStop received");
                Dispatcher.Invoke(() =>
                {
                    _movieWindow?.StopPlay();
                    _movieWindow = null;
                });
                break;

            // ─────── Phase 6.5: Remote Control ───────

            case MessageType.RemoteControlStart:
                // Phase 12-B — defensive release-all BEFORE starting a new
                // session.  Covers the case where a prior session ended
                // abruptly (teacher killed, network blip) and modifiers /
                // buttons were left "held" in the OS.
                RemoteControlReceiver.ReleaseAll();
                Dispatcher.Invoke(() =>
                {
                    if (_remoteBanner == null)
                    {
                        _remoteBanner = new RemoteControlBanner();
                        // Phase 12-B — banner close (user X-es it, OS closes it,
                        // window destroyed during shutdown) MUST release any
                        // held state so it can't outlive the visual signal.
                        _remoteBanner.Closed += (_, _) =>
                        {
                            _remoteBanner = null;
                            RemoteControlReceiver.ReleaseAll();
                        };
                        _remoteBanner.Show();
                        App.Tray?.ShowBalloon(Loc.Get("Lbl_AppName"), Loc.Get("Banner_RemoteActive"));
                    }
                });
                break;

            case MessageType.RemoteControlEnd:
                Dispatcher.Invoke(() =>
                {
                    _remoteBanner?.Close();
                    _remoteBanner = null;
                });
                // Phase 12-B — explicit release after the banner is torn down
                // (banner.Closed will also fire ReleaseAll above; calling here
                // makes the End path self-contained even if the banner was
                // already null).
                RemoteControlReceiver.ReleaseAll();
                break;

            case MessageType.RemoteMouseMove:
                try
                {
                    var m = MessagePack.MessagePackSerializer.Deserialize<RemoteMouseMoveMessage>(env.Payload);
                    RemoteControlReceiver.HandleMouseMove(m);
                }
                catch { }
                break;

            case MessageType.RemoteMouseClick:
                try
                {
                    var m = MessagePack.MessagePackSerializer.Deserialize<RemoteMouseClickMessage>(env.Payload);
                    RemoteControlReceiver.HandleMouseClick(m);
                }
                catch { }
                break;

            case MessageType.RemoteMouseScroll:
                try
                {
                    var m = MessagePack.MessagePackSerializer.Deserialize<RemoteMouseScrollMessage>(env.Payload);
                    RemoteControlReceiver.HandleMouseScroll(m);
                }
                catch { }
                break;

            case MessageType.RemoteKey:
                try
                {
                    var m = MessagePack.MessagePackSerializer.Deserialize<RemoteKeyMessage>(env.Payload);
                    RemoteControlReceiver.HandleKey(m);
                }
                catch { }
                break;

            // Phase 12-C step 2 — composed Unicode text from teacher (Thai/IME)
            case MessageType.RemoteText:
                try
                {
                    var m = MessagePack.MessagePackSerializer.Deserialize<RemoteTextMessage>(env.Payload);
                    RemoteControlReceiver.HandleRemoteText(m);
                }
                catch { }
                break;

            // ─────── Phase 4.6: Mic Monitor (always-on talkback) ───────

            case MessageType.MicMonitorStart:
                Dispatcher.Invoke(() =>
                {
                    if (_studentAudio == null)
                    {
                        _studentAudio = new StudentAudioBroadcaster();
                        _studentAudio.Start();
                        _micOn = true;
                        UpdateMicButtonText();
                    }
                });
                break;

            case MessageType.MicMonitorStop:
                Dispatcher.Invoke(() =>
                {
                    if (_studentAudio != null)
                    {
                        _studentAudio.Stop();
                        _studentAudio.Dispose();
                        _studentAudio = null;
                        _micOn = false;
                        UpdateMicButtonText();
                    }
                });
                break;

            // ─────── Phase 4 Part 2: Teacher viewing my screen ───────

            case MessageType.StudentStreamStart:
                {
                    var codec = VideoCodec.Mjpeg;
                    if (env.Payload != null && env.Payload.Length > 0)
                    {
                        try
                        {
                            var req = MessagePack.MessagePackSerializer.Deserialize<StudentStreamStartRequest>(env.Payload);
                            codec = req.Codec;
                        }
                        catch (Exception ex)
                        {
                            IpcClient.LogToFile($"[MainWindow] StudentStreamStart payload parse failed: {ex.Message}");
                        }
                    }
                    IpcClient.LogToFile($"[MainWindow] Student stream START (codec={codec})");
                    Dispatcher.Invoke(() =>
                    {
                        if (_studentBroadcaster == null)
                        {
                            _studentBroadcaster = new StudentBroadcaster { Codec = codec };
                            _studentBroadcaster.Start();
                            AddSystemNotification(Loc.Get("Chat_TeacherViewingScreen"), "👁");
                        }
                    });
                }
                break;

            case MessageType.StudentStreamStop:
                IpcClient.LogToFile("[MainWindow] Student stream STOP");
                Dispatcher.Invoke(() =>
                {
                    if (_studentBroadcaster != null)
                    {
                        // Phase 13-C (Tier 2) — if I'm currently the group
                        // host, the broadcaster must keep running for the
                        // group fan-out.  Teacher's view stop just stops
                        // observing; the group share continues.
                        if (_studentBroadcaster.GroupBroadcastGroupId.HasValue)
                        {
                            IpcClient.LogToFile("[MainWindow] Teacher view stopped, broadcaster kept alive for group host role");
                            AddSystemNotification(Loc.Get("Chat_TeacherStoppedViewing"), "👁");
                            return;
                        }
                        _studentBroadcaster.Stop();
                        _studentBroadcaster.Dispose();
                        _studentBroadcaster = null;
                        AddSystemNotification(Loc.Get("Chat_TeacherStoppedViewing"), "👁");
                    }
                });
                break;

            // ─────── Phase 4 Part 3a: Teacher audio broadcast ───────

            case MessageType.AudioStreamStart:
                IpcClient.LogToFile("[MainWindow] Audio stream START");
                Dispatcher.Invoke(() =>
                {
                    _audioPlayer ??= new AudioPlayer();
                    AddSystemNotification(Loc.Get("Chat_AudioStarted"), "🔊");
                });
                break;

            case MessageType.AudioStreamFrame:
                {
                    var frame = MessagePack.MessagePackSerializer.Deserialize<AudioStreamFrameMessage>(env.Payload);
                    Dispatcher.Invoke(() =>
                    {
                        _audioPlayer ??= new AudioPlayer();
                        _audioPlayer.PushFrame(frame.PcmData, frame.SampleRate, frame.Channels, frame.BitsPerSample);
                    });
                }
                break;

            case MessageType.AudioStreamStop:
                IpcClient.LogToFile("[MainWindow] Audio stream STOP");
                Dispatcher.Invoke(() =>
                {
                    _audioPlayer?.Stop();
                    _audioPlayer?.Dispose();
                    _audioPlayer = null;
                    AddSystemNotification(Loc.Get("Chat_AudioStopped"), "🔇");
                });
                break;

            // ─────── Phase 13: Exam System ───────

            case MessageType.QuizStart:
                {
                    try
                    {
                        var payload = MessagePack.MessagePackSerializer.Deserialize<ClassroomCtrl.Exam.Shared.QuizStartPayload>(env.Payload);
                        IpcClient.LogToFile($"[MainWindow] QuizStart: {payload.Exam.Title} ({payload.Exam.Questions.Count} q)");
                        Dispatcher.Invoke(() =>
                        {
                            if (_examWindow != null)
                            {
                                IpcClient.LogToFile("[MainWindow] QuizStart ignored — exam already open");
                                return;
                            }
                            _examWindow = new LockdownExamWindow(payload.Exam, payload.SessionId);
                            _examWindow.Closed += (_, _) => _examWindow = null;
                            _examWindow.Show();
                        });
                    }
                    catch (Exception ex)
                    {
                        IpcClient.LogToFile($"[MainWindow] QuizStart parse failed: {ex.Message}");
                    }
                }
                break;

            case MessageType.QuizEnd:
            case MessageType.QuizUnlockEarly:
                IpcClient.LogToFile($"[MainWindow] {env.Type} received — closing lockdown");
                Dispatcher.Invoke(() =>
                {
                    _examWindow?.Close();
                    _examWindow = null;
                });
                break;

            // ─────── Phase 4 Part 3c: Teacher master mute ───────

            case MessageType.ForceMuteStudentMic:
                IpcClient.LogToFile("[MainWindow] ForceMuteStudentMic from teacher");
                Dispatcher.Invoke(() =>
                {
                    if (_micOn)
                    {
                        _studentAudio?.Stop();
                        _studentAudio?.Dispose();
                        _studentAudio = null;
                        _micOn = false;
                        UpdateMicButtonText();
                    }
                    AddSystemNotification(Loc.Get("Chat_TeacherForcedMute"), "🔇");
                });
                break;
        }
    }

    /// <summary>Lazily create the fullscreen viewer window.</summary>
    private void EnsureScreenViewWindow()
    {
        if (_screenViewWindow == null)
        {
            _screenViewWindow = new ScreenViewWindow();
            _screenViewWindow.Closed += (_, _) => _screenViewWindow = null;
            _screenViewWindow.Show();
        }
    }

    // Phase 13-C (Tier 2) — true iff THIS handler is the one that started
    // the broadcaster (teacher wasn't already viewing).  Used by Stop to
    // know whether disposing the broadcaster is safe or whether teacher's
    // view path still owns the lifecycle.
    private bool _broadcasterOwnedByGroupHost;

    /// <summary>Phase 13-C (Tier 2) — host transition handler.  Sets the
    /// group-broadcast target on the broadcaster (creating + starting it if
    /// teacher wasn't already viewing) so every captured frame is emitted
    /// both as StudentStreamFrame (teacher view, if active) AND as
    /// StudentGroupScreenStreamFrame (in-group fan-out).</summary>
    private void StartGroupHostBroadcast(Guid groupId, VideoCodec codec)
    {
        if (_studentBroadcaster == null)
        {
            _studentBroadcaster = new StudentBroadcaster
            {
                Codec = codec,
                GroupBroadcastGroupId = groupId,
            };
            _studentBroadcaster.Start();
            _broadcasterOwnedByGroupHost = true;
            IpcClient.LogToFile($"[MainWindow] Group host broadcaster STARTED for group {groupId} (codec={codec})");
        }
        else
        {
            // Teacher is already viewing — broadcaster runs already, just
            // attach the group-broadcast target.  Codec mismatch (teacher
            // requested MJPEG, host got H.264) keeps the running encoder.
            _studentBroadcaster.GroupBroadcastGroupId = groupId;
            IpcClient.LogToFile($"[MainWindow] Group host attached to in-flight broadcaster, group={groupId}");
        }
    }

    /// <summary>Phase 13-C (Tier 2) — host-role revoked.  Detach the group
    /// target so frames stop fanning out to the group.  If we owned the
    /// broadcaster lifecycle (teacher wasn't viewing when we started it),
    /// also stop + dispose.  Otherwise let teacher's StudentStreamStop
    /// handler tear it down later.</summary>
    private void StopGroupHostBroadcast()
    {
        if (_studentBroadcaster == null) return;
        _studentBroadcaster.GroupBroadcastGroupId = null;
        if (_broadcasterOwnedByGroupHost)
        {
            _studentBroadcaster.Stop();
            _studentBroadcaster.Dispose();
            _studentBroadcaster = null;
            _broadcasterOwnedByGroupHost = false;
            IpcClient.LogToFile("[MainWindow] Group host broadcaster STOPPED");
        }
        else
        {
            IpcClient.LogToFile("[MainWindow] Group host detached, broadcaster left running for teacher view");
        }
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Phase 6.6: One-shot full-resolution screenshot. Spans the entire virtual screen
    /// (multi-monitor friendly), JPEG-encoded at 90% quality, sent back via IPC.
    /// </summary>
    private async Task HandleScreenshotRequestAsync(Envelope incoming)
    {
        try
        {
            var bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            byte[] jpegBytes;

            using (var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height))
            {
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size,
                        System.Drawing.CopyPixelOperation.SourceCopy);
                }

                using var ms = new System.IO.MemoryStream();
                var jpegEncoder = System.Drawing.Imaging.ImageCodecInfo.GetImageEncoders()
                    .FirstOrDefault(e => e.MimeType == "image/jpeg");
                if (jpegEncoder != null)
                {
                    var encParams = new System.Drawing.Imaging.EncoderParameters(1);
                    encParams.Param[0] = new System.Drawing.Imaging.EncoderParameter(
                        System.Drawing.Imaging.Encoder.Quality, 90L);
                    bmp.Save(ms, jpegEncoder, encParams);
                }
                else
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg);
                }
                jpegBytes = ms.ToArray();
            }

            var resp = new ScreenshotResponseMessage
            {
                StudentId = Guid.Empty, // Service rewrites SenderId; teacher reads from env.SenderId
                JpegData = jpegBytes,
                Width = bounds.Width,
                Height = bounds.Height,
                CapturedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            var bytes = MessagePack.MessagePackSerializer.Serialize(resp);
            var env = Envelope.Create(MessageType.ScreenshotResponse, bytes, Guid.Empty);
            if (App.Ipc != null)
                await App.Ipc.SendAsync(env);

            IpcClient.LogToFile($"[MainWindow] Screenshot captured {bounds.Width}x{bounds.Height} -> {jpegBytes.Length} bytes");
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[MainWindow] Screenshot capture failed: {ex.Message}");
        }
    }

    private async void RaiseHand_Click(object sender, RoutedEventArgs e)
    {
        IpcClient.LogToFile("[MainWindow] RaiseHand_Click fired");

        _handRaised = !_handRaised;

        var msg = new HandRaiseMessage
        {
            StudentId = System.Guid.Empty,
            StudentName = System.Environment.UserName,
            IsRaised = _handRaised,
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
        var env = Envelope.Create(
            _handRaised ? MessageType.HandRaise : MessageType.HandLower,
            bytes, System.Guid.Empty);

        if (App.Ipc != null)
        {
            await App.Ipc.SendAsync(env);
        }

        AddSystemNotification(Loc.Get(_handRaised ? "Chat_YouRaisedHand" : "Chat_YouLoweredHand"), "✋");
    }

    private void ToggleMic_Click(object sender, RoutedEventArgs e) => ToggleMicrophone();

    /// <summary>Phase 9 Section A — public so ScreenViewWindow's bottom overlay
    /// can drive the same mic toggle without holding its own audio pipeline.</summary>
    public bool IsMicOn => _micOn;

    /// <summary>Phase 9 Section A — single source of truth for mic on/off,
    /// callable from MainWindow's button OR from the screen-share overlay.</summary>
    public void ToggleMicrophone()
    {
        if (_micOn)
        {
            _studentAudio?.Stop();
            _studentAudio?.Dispose();
            _studentAudio = null;
            _micOn = false;
            AddSystemNotification(Loc.Get("Chat_StudentMicOff"), "🔇");
        }
        else
        {
            if (!StudentAudioBroadcaster.HasMicrophone())
            {
                AddSystemNotification(Loc.Get("Err_NoMicrophone"), "⚠️");
                return;
            }
            _studentAudio = new StudentAudioBroadcaster();
            _studentAudio.Start();
            _micOn = true;
            AddSystemNotification(Loc.Get("Chat_StudentMicOn"), "🎤");
        }
        UpdateMicButtonText();
        MicStateChanged?.Invoke(this, _micOn);
    }

    /// <summary>Phase 9 Section A — fires after every mic-state transition so
    /// the screen-share overlay can refresh its own button label icon.</summary>
    public event EventHandler<bool>? MicStateChanged;

    /// <summary>Phase 9 Section A — sends a chat line as if it were typed in
    /// the MainWindow input box, so the overlay can short-circuit straight to
    /// SendChat() without reproducing the envelope-construction logic.</summary>
    public void SendChatExternal(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        ChatInput.Text = text;
        SendChat();
    }

    // Phase 9.1 Section D — Voice button removed.  The 3-button action grid
    // collapsed to 2 columns (Raise Hand + Mic).  The breakout-room voice
    // channel was always coupled to the mic broadcaster anyway (Phase 9 C
    // analysis: ToggleVoice auto-started _studentAudio if not running), so
    // the simplification doesn't lose talkback in breakout rooms — the same
    // mic stream reaches peers in the same room.  Phase 13-B step 1 removed the
    // never-wired RoomVoiceJoin/Leave placeholders entirely; Tier 3 group voice
    // uses fresh codepoints in the 0x064x range with a dedicated _voiceOutbox
    // (see docs/breakout-rooms-tier3-design.md).

    // Phase 8 Section A — Settings_Click removed at customer request.  The
    // first-run TeacherIPDialog is still launched from App startup when no
    // config.txt exists; once configured, students cannot rewipe the IP from
    // the Agent UI.  Teacher's matching Phase 4 G AdminPasswordSettingsDialog
    // is also deleted in Phase 8 Section D so the password-gate machinery is
    // dead-code-only.

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new LanguageDialog { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedLanguage))
        {
            Loc.SetLanguage(dlg.SelectedLanguage);
            App.SavePreferredLanguage(dlg.SelectedLanguage);
        }
    }

    private void SendChat_Click(object sender, RoutedEventArgs e) => SendChat();

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SendChat();
    }

    private async void SendChat()
    {
        var text = ChatInput.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        var prefix = _myRoomId.HasValue ? "[Room] " : "";
        ChatList.Items.Add($"{prefix}[{Loc.Get("Chat_MePrefix")}] {text}");
        ChatInput.Clear();

        if (App.Ipc != null)
        {
            var msg = new ChatMessage
            {
                SenderName = System.Environment.UserName,
                Text = text,
                RoomId = _myRoomId,
                TimestampUtcMs = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
            var env = Envelope.Create(
                _myRoomId.HasValue ? MessageType.ChatRoom : MessageType.ChatBroadcast,
                bytes,
                System.Guid.Empty);
            await App.Ipc.SendAsync(env);
        }
    }
}