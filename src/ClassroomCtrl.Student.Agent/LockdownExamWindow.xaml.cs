using ClassroomCtrl.Exam.Shared;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 13d: Fullscreen always-on-top exam window.
///
/// Lockdown features:
///   - Topmost+borderless+maximized; watchdog timer reasserts every 100ms
///   - Low-level keyboard hook blocks Win key, Alt+Tab, Ctrl+Esc, Alt+F4, Alt+Esc
///   - Hides Windows taskbar (FindWindow Shell_TrayWnd + ShowWindow SW_HIDE)
///   - PreviewKeyDown swallows Tab/Escape inside the app
///
/// NOT blocked (security boundary): Ctrl+Alt+Del (SAS — protected by Windows itself),
/// physical power button, network disconnection.
///
/// On submit OR timer expiry: serializes Answers + sends QuizAnswerSubmit via IPC,
/// then waits for QuizEnd from teacher OR auto-closes after 5s on success.
/// </summary>
public partial class LockdownExamWindow : Window
{
    private readonly ExamModel _exam;
    private readonly Guid _sessionId;
    private readonly DateTime _startedAtUtc;

    private int _currentIdx;
    private readonly ObservableCollection<ChoiceVM> _choices = new();
    private readonly Dictionary<Guid, Answer> _answers = new();

    private DispatcherTimer? _timer;
    private DispatcherTimer? _watchdog;
    private TimeSpan _timeRemaining;

    // Win32 keyboard hook
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;
    private bool _submitted;

    // Phase 13f: student identity captured pre-test
    private StudentInfo _studentInfo = new();

    // Registry persist of student info — saves the school 30 keystrokes per quiz
    private const string RegPath = @"Software\NTY\ClassroomCtrl\StudentInfo";

    public LockdownExamWindow(ExamModel exam, Guid sessionId)
    {
        InitializeComponent();
        _exam = exam;
        _sessionId = sessionId;
        _startedAtUtc = DateTime.UtcNow;
        _timeRemaining = TimeSpan.FromMinutes(exam.TimeLimitMinutes);

        QuizTitleText.Text = exam.Title;
        InfoQuizSubtitle.Text = exam.Title;
        ChoicesList.ItemsSource = _choices;

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Force fullscreen — some show orderings get a default-sized window
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        WindowState = WindowState.Maximized;

        // Lock immediately — students should not be able to escape even on the info page.
        InstallKeyboardHook();
        HideTaskbar();

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _watchdog.Tick += (_, _) =>
        {
            try
            {
                Topmost = true;
                if (WindowState != WindowState.Maximized) WindowState = WindowState.Maximized;
            }
            catch { }
        };
        _watchdog.Start();

        // Phase 13f: prefill from last-used info if available; do NOT start the exam timer
        // until the student clicks Start.
        LoadStudentInfoFromRegistry();
        FullNameInput.Focus();

        Activate();
    }

    private void StartExamButton_Click(object sender, RoutedEventArgs e)
    {
        var name = FullNameInput.Text?.Trim() ?? "";
        var className = ClassInput.Text?.Trim() ?? "";
        var number = StudentNumberInput.Text?.Trim() ?? "";

        if (name.Length == 0 || className.Length == 0 || number.Length == 0)
        {
            InfoErrorText.Text = "⚠️ " + Loc.Get("Err_FillAllFields");
            InfoErrorText.Visibility = Visibility.Visible;
            return;
        }

        _studentInfo = new StudentInfo
        {
            FullName = name,
            ClassName = className,
            StudentNumber = number,
        };
        SaveStudentInfoToRegistry(_studentInfo);

        // Swap pages
        StudentInfoPage.Visibility = Visibility.Collapsed;
        ExamShell.Visibility = Visibility.Visible;

        // Start the exam now — timer counts down only after info is locked in.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        UpdateTimerText();
        ShowQuestion(0);
    }

    private void LoadStudentInfoFromRegistry()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath);
            if (key == null) return;
            FullNameInput.Text = key.GetValue("FullName") as string ?? "";
            ClassInput.Text = key.GetValue("ClassName") as string ?? "";
            StudentNumberInput.Text = key.GetValue("StudentNumber") as string ?? "";
        }
        catch (Exception ex) { IpcClient.LogToFile($"[Lockdown] LoadStudentInfo failed: {ex.Message}"); }
    }

    private static void SaveStudentInfoToRegistry(StudentInfo info)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath);
            key.SetValue("FullName", info.FullName);
            key.SetValue("ClassName", info.ClassName);
            key.SetValue("StudentNumber", info.StudentNumber);
        }
        catch (Exception ex) { IpcClient.LogToFile($"[Lockdown] SaveStudentInfo failed: {ex.Message}"); }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Swallow common navigation keys that would jump focus or close the window
        if (e.Key == Key.Tab || e.Key == Key.Escape || e.Key == Key.LWin || e.Key == Key.RWin)
        {
            e.Handled = true;
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timeRemaining = _timeRemaining.Subtract(TimeSpan.FromSeconds(1));
        if (_timeRemaining.TotalSeconds <= 0)
        {
            _timeRemaining = TimeSpan.Zero;
            UpdateTimerText();
            _ = AutoSubmitAsync();
            return;
        }
        UpdateTimerText();
    }

    private void UpdateTimerText()
    {
        TimerText.Text = $"{(int)_timeRemaining.TotalMinutes:D2}:{_timeRemaining.Seconds:D2}";
    }

    private void ShowQuestion(int idx)
    {
        // Save current answer first
        SaveCurrentAnswer();

        if (_exam.Questions.Count == 0) return;
        idx = Math.Clamp(idx, 0, _exam.Questions.Count - 1);
        _currentIdx = idx;

        var q = _exam.Questions[idx];
        QuestionNumberText.Text = Loc.Format("Lbl_QuestionN", idx + 1, _exam.Questions.Count);
        QuestionText.Text = q.Text;
        ProgressText.Text = $"{idx + 1} / {_exam.Questions.Count}";

        _choices.Clear();
        ChoicesList.Visibility = Visibility.Collapsed;
        ShortAnswerBox.Visibility = Visibility.Collapsed;
        ShortAnswerBox.Text = "";

        var existing = _answers.GetValueOrDefault(q.Id);

        if (q.Type == QuestionType.ShortAnswer)
        {
            ShortAnswerBox.Visibility = Visibility.Visible;
            ShortAnswerBox.Text = existing?.EssayText ?? "";
        }
        else
        {
            ChoicesList.Visibility = Visibility.Visible;
            foreach (var c in q.Choices)
            {
                _choices.Add(new ChoiceVM
                {
                    Label = c.Label,
                    Text = c.Text,
                    IsSelected = existing != null && existing.SelectedLabels.Contains(c.Label, StringComparer.OrdinalIgnoreCase),
                });
            }
        }

        PrevBtn.IsEnabled = idx > 0;
        NextBtn.IsEnabled = idx < _exam.Questions.Count - 1;
    }

    private void SaveCurrentAnswer()
    {
        if (_exam.Questions.Count == 0) return;
        if (_currentIdx < 0 || _currentIdx >= _exam.Questions.Count) return;
        var q = _exam.Questions[_currentIdx];
        var ans = new Answer { QuestionId = q.Id };
        if (q.Type == QuestionType.ShortAnswer)
        {
            ans.EssayText = ShortAnswerBox.Text?.Trim() ?? "";
        }
        else
        {
            ans.SelectedLabels = _choices.Where(c => c.IsSelected).Select(c => c.Label).ToList();
        }
        _answers[q.Id] = ans;
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => ShowQuestion(_currentIdx - 1);
    private void Next_Click(object sender, RoutedEventArgs e) => ShowQuestion(_currentIdx + 1);

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(
            Loc.Get("Confirm_Submit"), Loc.Get("Btn_Submit"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        await SubmitAsync(isAutoSubmitted: false);
    }

    private async Task AutoSubmitAsync()
    {
        if (_submitted) return;
        await SubmitAsync(isAutoSubmitted: true);
    }

    private async Task SubmitAsync(bool isAutoSubmitted)
    {
        if (_submitted) return;
        _submitted = true;

        SaveCurrentAnswer();
        _timer?.Stop();

        var payload = new QuizAnswerSubmitPayload
        {
            SessionId = _sessionId,
            ExamId = _exam.Id,
            DisplayName = string.IsNullOrWhiteSpace(_studentInfo.FullName) ? Environment.UserName : _studentInfo.FullName,
            Student = _studentInfo,  // Phase 13f: pre-test student info
            Answers = _answers.Values.ToList(),
            IsAutoSubmitted = isAutoSubmitted,
            SubmittedAtUtc = DateTime.UtcNow,
        };
        var bytes = MessagePack.MessagePackSerializer.Serialize(payload);
        var env = Envelope.Create(MessageType.QuizAnswerSubmit, bytes, Guid.Empty);
        if (App.Ipc != null)
        {
            try { await App.Ipc.SendAsync(env); }
            catch (Exception ex) { IpcClient.LogToFile($"[Lockdown] submit send failed: {ex.Message}"); }
        }

        // Compute score locally for instant feedback (teacher will recompute authoritatively).
        int score = AutoGrader.ScoreSubmission(_exam, new Submission
        {
            Answers = _answers.Values.ToList()
        }, partialCreditForMulti: true);
        int maxScore = _exam.TotalPoints;
        int pct = maxScore > 0 ? (int)Math.Round(100.0 * score / maxScore) : 0;

        string msg;
        if (isAutoSubmitted)
            msg = Loc.Get("Msg_TimeUp");
        else if (_exam.ShowScoreToStudent)
            msg = Loc.Format("Msg_YourScore", score, maxScore, pct);
        else
            msg = Loc.Get("Msg_QuizSubmitted");

        QuestionPanel.Children.Clear();
        var resultText = new System.Windows.Controls.TextBlock
        {
            Text = msg,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 32,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 80, 0, 16),
            TextAlignment = TextAlignment.Center,
        };
        QuestionPanel.Children.Add(resultText);

        var waitText = new System.Windows.Controls.TextBlock
        {
            Text = Loc.Get("Msg_WaitForTeacher"),
            Foreground = System.Windows.Media.Brushes.LightGray,
            FontSize = 16,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };
        QuestionPanel.Children.Add(waitText);

        // Disable nav
        PrevBtn.IsEnabled = false;
        NextBtn.IsEnabled = false;
        SubmitBtn.IsEnabled = false;
        ChoicesList.Visibility = Visibility.Collapsed;
        ShortAnswerBox.Visibility = Visibility.Collapsed;

        // Auto-close after 8 seconds even if teacher doesn't send QuizEnd —
        // the watchdog still keeps us topmost so this is purely UX.
        var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        closeTimer.Tick += (_, _) => { closeTimer.Stop(); Close(); };
        closeTimer.Start();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer?.Stop();
        _watchdog?.Stop();
        UninstallKeyboardHook();
        ShowTaskbar();
    }

    // ─────── Keyboard hook (Win32) ───────

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private void InstallKeyboardHook()
    {
        try
        {
            _hookProc = HookCallback;
            using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            using var curModule = curProcess.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(curModule.ModuleName!), 0);
        }
        catch (Exception ex) { IpcClient.LogToFile($"[Lockdown] hook install failed: {ex.Message}"); }
    }

    private void UninstallKeyboardHook()
    {
        try
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
        catch { }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN))
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            uint vk = data.vkCode;

            // VK codes
            const uint VK_TAB = 0x09;
            const uint VK_ESCAPE = 0x1B;
            const uint VK_LWIN = 0x5B;
            const uint VK_RWIN = 0x5C;
            const uint VK_F4 = 0x73;

            bool altDown = (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Alt) != 0;
            bool ctrlDown = (System.Windows.Forms.Control.ModifierKeys & System.Windows.Forms.Keys.Control) != 0;

            if (vk == VK_LWIN || vk == VK_RWIN) return (IntPtr)1;
            if (altDown && vk == VK_TAB) return (IntPtr)1;
            if (altDown && vk == VK_ESCAPE) return (IntPtr)1;
            if (altDown && vk == VK_F4) return (IntPtr)1;
            if (ctrlDown && vk == VK_ESCAPE) return (IntPtr)1;
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    // ─────── Taskbar hide/show (Win32) ───────

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int ShowWindow(IntPtr hwnd, int command);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private static void HideTaskbar()
    {
        try
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero) ShowWindow(taskbar, SW_HIDE);
            var startBtn = FindWindow("Button", "Start");
            if (startBtn != IntPtr.Zero) ShowWindow(startBtn, SW_HIDE);
        }
        catch { }
    }

    private static void ShowTaskbar()
    {
        try
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar != IntPtr.Zero) ShowWindow(taskbar, SW_SHOW);
            var startBtn = FindWindow("Button", "Start");
            if (startBtn != IntPtr.Zero) ShowWindow(startBtn, SW_SHOW);
        }
        catch { }
    }

    public partial class ChoiceVM : ObservableObject
    {
        [ObservableProperty] private string label = "";
        [ObservableProperty] private string text = "";
        [ObservableProperty] private bool isSelected;
    }
}
