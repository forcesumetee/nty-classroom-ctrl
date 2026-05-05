using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Student.Agent.Dialogs;
using ClassroomCtrl.Student.Agent.Setup;
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

    // Phase 9.5: Camera view + Phase 9.6: Movie player + Phase 6.5: Remote banner
    private CameraViewWindow? _cameraWindow;
    private MoviePlayerWindow? _movieWindow;
    private RemoteControlBanner? _remoteBanner;

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
                    });
                }
                break;

            case MessageType.ChatDirect:
                {
                    var chat = MessagePack.MessagePackSerializer.Deserialize<ChatMessage>(env.Payload);
                    Dispatcher.Invoke(() =>
                    {
                        ChatList.Items.Add($"[DM from {chat.SenderName}] {chat.Text}");
                    });
                }
                break;

            case MessageType.ChatRoom:
                {
                    var chat = MessagePack.MessagePackSerializer.Deserialize<ChatMessage>(env.Payload);
                    Dispatcher.Invoke(() =>
                    {
                        ChatList.Items.Add($"[Room] [{chat.SenderName}] {chat.Text}");
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

                    Dispatcher.Invoke(() =>
                    {
                        ChatList.Items.Add(newRoom.HasValue
                            ? Loc.Format("Chat_MovedToRoom", assign.RoomName)
                            : Loc.Get("Chat_ReturnedToMain"));

                        if (iAmHost)
                        {
                            if (_hostToolbar == null)
                            {
                                _hostToolbar = new HostFloatingToolbar(_myEndpointId!.Value, assign.RoomName);
                                _hostToolbar.Show();
                                App.Tray?.ShowBalloon(Loc.Get("Lbl_AppName"),
                                    Loc.Format("Toast_YouAreHost", assign.RoomName));
                                ChatList.Items.Add(Loc.Format("Toast_YouAreHost", assign.RoomName));
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
                IpcClient.LogToFile("[MainWindow] Screen stream START");
                Dispatcher.Invoke(EnsureScreenViewWindow);
                break;

            case MessageType.ScreenStreamFrame:
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
                IpcClient.LogToFile("[MainWindow] Screen stream STOP");
                Dispatcher.Invoke(() =>
                {
                    _screenViewWindow?.Close();
                    _screenViewWindow = null;
                });
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
                            ChatList.Items.Add(Loc.Get("Toast_YouAreDemoing"));
                            return;
                        }
                        if (_demoPeerWindow == null)
                        {
                            _demoPeerWindow = new PeerScreenViewWindow(ds.SourceName);
                            _demoPeerWindow.Closed += (_, _) => _demoPeerWindow = null;
                            _demoPeerWindow.Show();
                            ChatList.Items.Add(Loc.Format("Lbl_DemoActive", ds.SourceName));
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
                    Dispatcher.Invoke(() =>
                    {
                        if (_movieWindow == null)
                        {
                            _movieWindow = new MoviePlayerWindow();
                            _movieWindow.Closed += (_, _) => _movieWindow = null;
                            _movieWindow.Show();
                        }
                        _movieWindow.Play(mp.FileName, mp.SeekTime, mp.PlayAtTimestampMs);
                    });
                }
                catch { }
                break;

            case MessageType.MoviePause:
                try
                {
                    var ms = MessagePack.MessagePackSerializer.Deserialize<MovieSeekMessage>(env.Payload);
                    Dispatcher.Invoke(() => _movieWindow?.Pause(ms.SeekTime));
                }
                catch { }
                break;

            case MessageType.MovieSeek:
                try
                {
                    var ms = MessagePack.MessagePackSerializer.Deserialize<MovieSeekMessage>(env.Payload);
                    Dispatcher.Invoke(() => _movieWindow?.Seek(ms.SeekTime));
                }
                catch { }
                break;

            case MessageType.MovieStop:
                Dispatcher.Invoke(() =>
                {
                    _movieWindow?.StopPlay();
                    _movieWindow = null;
                });
                break;

            // ─────── Phase 6.5: Remote Control ───────

            case MessageType.RemoteControlStart:
                Dispatcher.Invoke(() =>
                {
                    if (_remoteBanner == null)
                    {
                        _remoteBanner = new RemoteControlBanner();
                        _remoteBanner.Closed += (_, _) => _remoteBanner = null;
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
                            ChatList.Items.Add(Loc.Get("Chat_TeacherViewingScreen"));
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
                        _studentBroadcaster.Stop();
                        _studentBroadcaster.Dispose();
                        _studentBroadcaster = null;
                        ChatList.Items.Add(Loc.Get("Chat_TeacherStoppedViewing"));
                    }
                });
                break;

            // ─────── Phase 4 Part 3a: Teacher audio broadcast ───────

            case MessageType.AudioStreamStart:
                IpcClient.LogToFile("[MainWindow] Audio stream START");
                Dispatcher.Invoke(() =>
                {
                    _audioPlayer ??= new AudioPlayer();
                    ChatList.Items.Add(Loc.Get("Chat_AudioStarted"));
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
                    ChatList.Items.Add(Loc.Get("Chat_AudioStopped"));
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
                    ChatList.Items.Add(Loc.Get("Chat_TeacherForcedMute"));
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

        ChatList.Items.Add(Loc.Get(_handRaised ? "Chat_YouRaisedHand" : "Chat_YouLoweredHand"));
    }

    private void ToggleMic_Click(object sender, RoutedEventArgs e)
    {
        if (_micOn)
        {
            _studentAudio?.Stop();
            _studentAudio?.Dispose();
            _studentAudio = null;
            _micOn = false;
            ChatList.Items.Add(Loc.Get("Chat_StudentMicOff"));
        }
        else
        {
            if (!StudentAudioBroadcaster.HasMicrophone())
            {
                ChatList.Items.Add(Loc.Get("Err_NoMicrophone"));
                return;
            }
            _studentAudio = new StudentAudioBroadcaster();
            _studentAudio.Start();
            _micOn = true;
            ChatList.Items.Add(Loc.Get("Chat_StudentMicOn"));
        }
        UpdateMicButtonText();
    }

    // Phase 9.7: Voice Chat — toggle membership in breakout-room voice channel.
    private bool _voiceJoined;

    private async void ToggleVoice_Click(object sender, RoutedEventArgs e)
    {
        if (App.Ipc == null) return;
        _voiceJoined = !_voiceJoined;

        var env = Envelope.Create(
            _voiceJoined ? MessageType.RoomVoiceJoin : MessageType.RoomVoiceLeave,
            System.Array.Empty<byte>(), System.Guid.Empty);
        await App.Ipc.SendAsync(env);

        if (_voiceJoined && _studentAudio == null && StudentAudioBroadcaster.HasMicrophone())
        {
            _studentAudio = new StudentAudioBroadcaster();
            _studentAudio.Start();
            _micOn = true;
            UpdateMicButtonText();
        }
        VoiceButton.Content = Loc.Get(_voiceJoined ? "Btn_LeaveVoice" : "Btn_JoinVoice");
        ChatList.Items.Add(Loc.Get(_voiceJoined ? "Chat_VoiceJoined" : "Chat_VoiceLeft"));
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        // Phase 14.2: gate connection settings behind an admin password so students
        // can't wipe the configured Teacher IP.
        var auth = new ClassroomCtrl.Student.Agent.Setup.AdminPasswordDialog { Owner = this };
        if (auth.ShowDialog() != true || !auth.Authenticated) return;

        var dlg = new TeacherIPDialog { Owner = this };
        dlg.ShowDialog();

        if (dlg.Saved)
        {
            ChatList.Items.Add(Loc.Get("Chat_TeacherIPUpdated"));
            MessageBox.Show(
                Loc.Get("Dlg_TeacherIPSavedMsg"),
                Loc.Get("Dlg_SettingsSaved"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

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