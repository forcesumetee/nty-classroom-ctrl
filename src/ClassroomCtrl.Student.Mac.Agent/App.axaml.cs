using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Student.Mac.Agent.Services;
using ClassroomCtrl.Student.Mac.Agent.ViewModels;
using ClassroomCtrl.Student.Mac.Agent.Views;
using MessagePack;

namespace ClassroomCtrl.Student.Mac.Agent;

public partial class App : Application
{
    private AgentViewModel? _vm;
    private DaemonIpcClient? _ipc;
    private readonly ChatViewModel _chatVm = new();
    private ChatWindow? _chatWindow;
    private readonly BroadcastViewModel _broadcastVm = new();
    private BroadcastWindow? _broadcastWindow;
    private bool _broadcasting;
    private readonly QuizViewModel _quizVm = new();
    private QuizWindow? _quizWindow;
    private bool _shuttingDown;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Menubar-only agent: no main window, so the app must not exit until the tray's Quit.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            _vm = new AgentViewModel(openDownloads: OpenDownloadsFolder, openChat: () => ShowChat(activate: true),
                                     quit: () => desktop.Shutdown());
            DataContext = _vm;   // the TrayIcon + NativeMenu in App.axaml bind to this

            // A student reply → relay to the daemon over IPC (it stamps identity + sends to the Teacher).
            _chatVm.MessageSent += OnChatMessageSent;
            _quizVm.AnswerSubmitted += OnQuizAnswerSubmitted;

            _ipc = new DaemonIpcClient();
            _ipc.Connected += () => OnUi(() => _vm!.SetIpcConnected(true));
            _ipc.Disconnected += () => OnUi(() => _vm!.SetIpcConnected(false));
            _ipc.EnvelopeReceived += env => OnUi(() => HandleEnvelope(env));
            _ipc.Start();

            desktop.ShutdownRequested += (_, _) => { _shuttingDown = true; _ipc?.Dispose(); };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Marshal an action onto the Avalonia UI thread (IPC events arrive on a background thread).</summary>
    private static void OnUi(Action action) => Dispatcher.UIThread.InvokeAsync(action);

    // ── IPC envelope → UI (runs on the UI thread) ──

    private void HandleEnvelope(Envelope env)
    {
        switch (env.Type)
        {
            case MessageType.FileReceivedNotify:
                HandleFileNotify(env);
                break;

            case MessageType.TeacherStatusNotify:
                HandleTeacherStatus(env);
                break;

            case MessageType.LockScreen:
                _vm?.SetLocked(true);
                ToastWindow.Show("Screen locked", "Your screen was locked by the teacher.", ToastKind.Info);
                break;

            case MessageType.UnlockScreen:
                _vm?.SetLocked(false);
                ToastWindow.Show("Screen unlocked", "Your screen was unlocked by the teacher.", ToastKind.Info);
                break;

            case MessageType.ChatBroadcast:
            case MessageType.ChatDirect:
                HandleChat(env);
                break;

            case MessageType.ScreenStreamStart:
                _broadcasting = true;
                _broadcastVm.Begin();
                ShowBroadcast();
                break;

            case MessageType.ScreenStreamFrame:
                if (_broadcasting) _broadcastVm.OnFrame(env.Payload);   // gated: ignore stray frames outside a session
                break;

            case MessageType.ScreenStreamStop:
                CloseBroadcast();
                break;

            case MessageType.QuizBroadcast:
                HandleQuiz(env);
                break;
        }
    }

    // ── Quiz ──

    private void HandleQuiz(Envelope env)
    {
        QuizQuestionMessage q;
        try { q = MessagePackSerializer.Deserialize<QuizQuestionMessage>(env.Payload); }
        catch { return; }
        if (q.Options.Count == 0) return;

        _quizVm.Load(q);
        ShowQuiz();
    }

    private void ShowQuiz()
    {
        if (_quizWindow is null)
        {
            _quizWindow = new QuizWindow { DataContext = _quizVm };
            _quizWindow.Closed += (_, _) => _quizWindow = null;   // transient — recreated per quiz
        }
        if (!_quizWindow.IsVisible) _quizWindow.Show();
        _quizWindow.WindowState = WindowState.Normal;
        _quizWindow.Activate();
    }

    private void CloseQuiz() => _quizWindow?.Close();

    private void OnQuizAnswerSubmitted(QuizSubmitRequestMessage msg)
        => _ = _ipc?.SendAsync(MessageType.QuizSubmitRequest, MessagePackSerializer.Serialize(msg));

    // ── Screen broadcast ──

    private void ShowBroadcast()
    {
        if (_broadcastWindow is null)
        {
            _broadcastWindow = new BroadcastWindow { DataContext = _broadcastVm };
            _broadcastWindow.Closed += (_, _) =>   // Esc / teacher-stop / disconnect
            {
                _broadcastWindow = null;
                _broadcasting = false;
                _broadcastVm.Reset();
            };
        }
        if (!_broadcastWindow.IsVisible) _broadcastWindow.Show();
        _broadcastWindow.Activate();
    }

    private void CloseBroadcast()
    {
        _broadcasting = false;
        if (_broadcastWindow is not null) _broadcastWindow.Close();   // Closed handler resets the VM
        else _broadcastVm.Reset();
    }

    // ── Chat ──

    private void HandleChat(Envelope env)
    {
        ChatMessage msg;
        try { msg = MessagePackSerializer.Deserialize<ChatMessage>(env.Payload); }
        catch { return; }
        if (string.IsNullOrEmpty(msg.Text)) return;

        _chatVm.AppendIncoming(msg.SenderName, msg.Text);
        ShowChat(activate: false);   // open on first message; don't steal focus if already open
    }

    private void OnChatMessageSent(string text)
        => _ = _ipc?.SendAsync(MessageType.ChatSendRequest,
                               MessagePackSerializer.Serialize(new ChatSendRequestMessage { Text = text }));

    /// <summary>Show the (single) chat window, creating it on first use. Closing it just hides it.</summary>
    private void ShowChat(bool activate)
    {
        if (_chatWindow is null)
        {
            _chatWindow = new ChatWindow { DataContext = _chatVm };
            _chatWindow.Closing += (_, e) =>
            {
                if (_shuttingDown) return;   // let the app close it on Quit
                e.Cancel = true;
                _chatWindow.Hide();
            };
        }
        if (!_chatWindow.IsVisible) _chatWindow.Show();
        if (activate) { _chatWindow.WindowState = WindowState.Normal; _chatWindow.Activate(); }
    }

    private void HandleTeacherStatus(Envelope env)
    {
        TeacherStatusMessage msg;
        try { msg = MessagePackSerializer.Deserialize<TeacherStatusMessage>(env.Payload); }
        catch { return; }
        _vm?.SetTeacherStatus(msg.Connected, msg.TeacherName);
        if (!msg.Connected) { CloseBroadcast(); CloseQuiz(); }   // teacher gone → tear down transient windows
    }

    private static void HandleFileNotify(Envelope env)
    {
        FileReceivedNotifyMessage msg;
        try { msg = MessagePackSerializer.Deserialize<FileReceivedNotifyMessage>(env.Payload); }
        catch { return; }

        switch ((FileNotifyStatus)msg.Status)
        {
            case FileNotifyStatus.Saved:
                ToastWindow.Show("File received from teacher", msg.FileName, ToastKind.Success);
                break;
            case FileNotifyStatus.Failed:
                ToastWindow.Show("File transfer failed",
                    string.IsNullOrWhiteSpace(msg.Error) ? msg.FileName : $"{msg.FileName} — {msg.Error}",
                    ToastKind.Warning);
                break;
            // Receiving: no toast — the Saved/Failed notification is the one that matters.
        }
    }

    // ── Menu actions ──

    /// <summary>Open ~/Downloads/NTY ClassroomCtrl in Finder (creating it if the folder is absent).</summary>
    private static void OpenDownloadsFolder()
    {
        var dir = FileReceiver.DefaultSaveDir();
        try
        {
            Directory.CreateDirectory(dir);
            var psi = new ProcessStartInfo { FileName = "open", UseShellExecute = false };
            psi.ArgumentList.Add(dir);   // ArgumentList escapes the space in "NTY ClassroomCtrl"
            using var _ = Process.Start(psi);
        }
        catch
        {
            // A Finder that won't open is not worth crashing the agent over.
        }
    }
}
