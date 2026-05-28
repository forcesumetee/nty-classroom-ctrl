using ClassroomCtrl.Licensing;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Teacher.Activation;
using ClassroomCtrl.Teacher.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Net;
using System.Windows;

namespace ClassroomCtrl.Teacher;

public partial class App : Application
{
    public static ControlServer? Server { get; private set; }
    public static ScreenBroadcaster? ScreenBroadcaster { get; private set; }
    public static AudioBroadcaster? AudioBroadcaster { get; private set; }
    public static StudentAudioMixer? StudentAudioMixer { get; private set; }
    public static AdaptiveBitrateController? AdaptiveBitrate { get; private set; }
    public static RecordingService? Recording { get; private set; }
    /// <summary>Phase 13: Quiz manager + session state.</summary>
    public static ExamService? Exam { get; private set; }
    /// <summary>Phase 5b: Per-student recording manager (one ffmpeg per student).</summary>
    public static PerStudentRecordingService? PerStudentRecording { get; private set; }
    /// <summary>Phase 9.3: Class roster manager.</summary>
    public static ClassRosterService? Roster { get; private set; }
    /// <summary>Phase 9.4: UDP discovery beacon (broadcasts to students every 2 sec).</summary>
    public static TeacherBeaconService? Beacon { get; private set; }
    /// <summary>Phase 14: Append-only audit log (best-effort).</summary>
    public static AuditLogService? Audit { get; private set; }
    /// <summary>Phase 1: Offline telemetry (counters → telemetry.json).</summary>
    public static TelemetryService? Telemetry { get; private set; }
    /// <summary>Phase 9.5: Webcam capture + JPEG broadcast.</summary>
    public static CameraBroadcastService? Camera { get; private set; }

    /// <summary>Phase 10.14 (Item 9): minimal tray notifier — provides Windows-toast
    /// surface for incoming student chat when Teacher window is hidden / minimized /
    /// unfocused.  See <see cref="TrayNotifier"/>.</summary>
    public static TrayNotifier? Notifier { get; private set; }

    /// <summary>Phase 4 Part 4: Global video codec selection. Read by encoders on Start.
    /// Phase 11-B inc4: default flipped MJPEG→H.264.  With UseHardwareH264 still default-OFF,
    /// this resolves to OpenH264Encoder (the validated SW H.264 path).  H.264 is required
    /// to scale to 25 students at 20 FPS — MJPEG would saturate the teacher's NIC.
    /// MJPEG remains selectable via the codec ComboBox for low-FPS / compatibility cases.</summary>
    public static VideoCodec SelectedCodec { get; set; } = VideoCodec.H264;

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    protected override async void OnStartup(StartupEventArgs e)
    {
        // Belt-and-suspenders DPI awareness. The app.manifest is the canonical
        // declaration (PerMonitorV2), but a manifest-less binary distribution path
        // (e.g. dotnet run) sometimes ignores the manifest, so we also call the
        // runtime API. PerMonitorV2 = 2 in the SHCORE enum.
        try { SetProcessDpiAwareness(2); }
        catch
        {
            try { SetProcessDPIAware(); } catch { /* old Windows — accept system DPI */ }
        }

        base.OnStartup(e);

        // Phase 9.1 Section A — auto-add Windows Firewall inbound rules so a
        // fresh install on a Public-profile network doesn't silently block
        // student connections.  Fire-and-forget on a worker thread; the main
        // startup path mustn't wait for the (potential) UAC prompt.  Idempotent
        // — netsh skips re-adding rules that already exist.
        System.Threading.Tasks.Task.Run(() => FirewallService.EnsureRules());

        var preferred = ReadPreferredLanguage();
        Loc.Initialize(preferred);
        Loc.LanguageChanged += ApplyLocToResources;
        ApplyLocToResources();

        if (!LicenseStore.IsCurrentMachineActivated())
        {
            var activation = new ActivationWindow();
            var ok = activation.ShowDialog();
            if (ok != true || !activation.ActivationSucceeded)
            {
                Shutdown();
                return;
            }
        }

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Information));
        var serverLogger = loggerFactory.CreateLogger<ControlServer>();
        Server = new ControlServer(serverLogger, loggerFactory, IPAddress.Any);
        try
        {
            await Server.StartAsync(System.Threading.CancellationToken.None);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Cannot start control server: {ex.Message}");
            Shutdown();
            return;
        }

        // Phase 4: Screen broadcaster (lazy — starts when teacher clicks "Share My Screen")
        var bcLogger = loggerFactory.CreateLogger<ScreenBroadcaster>();
        ScreenBroadcaster = new ScreenBroadcaster(bcLogger, Server);

        // Phase 4 Part 3a: Audio broadcaster (lazy — starts when teacher unmutes)
        var audioLogger = loggerFactory.CreateLogger<AudioBroadcaster>();
        AudioBroadcaster = new AudioBroadcaster(audioLogger, Server);

        // Phase 4 Part 3b: Student audio talkback mixer (always listening for incoming frames)
        var mixerLogger = loggerFactory.CreateLogger<StudentAudioMixer>();
        StudentAudioMixer = new StudentAudioMixer(mixerLogger);
        Server.StudentAudioStreamStarted += (_, id) => StudentAudioMixer.AddStudent(id);
        Server.StudentAudioFrameReceived += (_, e) => StudentAudioMixer.PushFrame(e.StudentId, e.Frame);
        Server.StudentAudioStreamStopped += (_, id) => StudentAudioMixer.RemoveStudent(id);
        Server.StudentLeft += (_, id) => StudentAudioMixer.RemoveStudent(id);

        // Phase 4 Part 5: Adaptive bitrate controller (always running)
        var abrLogger = loggerFactory.CreateLogger<AdaptiveBitrateController>();
        AdaptiveBitrate = new AdaptiveBitrateController(abrLogger);
        Server.QualityReportReceived += (_, e) => AdaptiveBitrate.OnReportReceived(e.StudentId, e.Report);
        Server.StudentLeft += (_, id) => AdaptiveBitrate.RemoveStudent(id);

        // Phase 5a + bug-fix: Recording service uses raw BGRA pipe to ffmpeg (Python pattern).
        // RawFrameCaptured event fires only when TeeRawFrames=true (set by RecordingService.Start).
        var recLogger = loggerFactory.CreateLogger<RecordingService>();
        Recording = new RecordingService(recLogger);
        ScreenBroadcaster.RawFrameCaptured += (_, e) => Recording.OnRawFrame(e.Bgra, e.Width, e.Height);
        AudioBroadcaster.AudioFrameProduced += (_, e) => Recording.OnAudioFrame(e.Pcm, e.SampleRate, e.Channels, e.BitsPerSample);
        _ = System.Threading.Tasks.Task.Run(() => RecordingService.WarmUpFFmpeg(recLogger));

        // Phase 13: Exam System.
        Exam = new ExamService();
        Server.QuizSubmissionReceived += (_, payload) => Exam.HandleSubmission(payload);

        // Phase 5b: Per-student recording manager.
        var perStudentLogger = loggerFactory.CreateLogger<PerStudentRecordingService>();
        PerStudentRecording = new PerStudentRecordingService(perStudentLogger);

        // Phase 9.3: Class roster manager.
        Roster = new ClassRosterService();

        // Phase 9.4: UDP discovery beacon (port 7778). Students auto-connect by ChannelId.
        var beaconLogger = loggerFactory.CreateLogger<TeacherBeaconService>();
        Beacon = new TeacherBeaconService(beaconLogger);
        Beacon.Start();

        // Phase 14: Audit log
        Audit = new AuditLogService();
        Audit.Log("TeacherStarted");
        Server.StudentJoined += (_, hello) => Audit?.Log("StudentJoined", new { hello.MachineName, hello.DisplayName });
        Server.StudentLeft += (_, id) => Audit?.Log("StudentLeft", new { Id = id });

        // Phase 3.5: Sound feedback for chat/hand/connect events
        SoundService.Initialize();
        Server.ChatReceived += (_, _) => SoundService.Play(SoundEvent.ChatDing);
        Server.HandRaiseReceived += (_, _) => SoundService.Play(SoundEvent.HandRaise);
        Server.StudentJoined += (_, _) => SoundService.Play(SoundEvent.StudentConnect);

        // Phase 9.5: Camera Broadcast
        var camLogger = loggerFactory.CreateLogger<CameraBroadcastService>();
        Camera = new CameraBroadcastService(Server, camLogger);

        // Phase 1: Telemetry (offline only)
        Telemetry = new TelemetryService();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { Telemetry?.RecordError(e.ExceptionObject?.ToString() ?? ""); } catch { }
        };

        // Phase 11.3: Branding (live apply via DynamicResource swap, mirroring Loc.cs pattern)
        // Phase 6C: also restores the persisted Light/Dark theme before brand colors land.
        // Phase 4 Section D: Light is now the default surface palette.  Existing branding.json
        // files written under Phase 8 still carry ThemeMode="Dark"; we override at startup so
        // the new design language ships consistently.  Runtime ApplyTheme is still wired so
        // the Branding dialog can flip back if a future toggle re-enables dark.
        ClassroomCtrl.Shared.Branding.BrandingService.Initialize();
        ClassroomCtrl.Shared.Branding.BrandingService.ApplyTheme(
            ClassroomCtrl.Shared.Branding.ThemeMode.Light);
        ClassroomCtrl.Shared.Branding.BrandingService.Changed += ApplyBrandingToResources;
        ApplyBrandingToResources();

        // Phase 10.14 (Item 9) — tray notifier (notification-only).  Init after
        // Branding.ApplyTheme so the pack:// URI for classroom_icon.ico resolves
        // against a fully-loaded App.Resources tree.
        Notifier = new TrayNotifier();
        Notifier.Initialize();

        var main = new MainWindow();
        main.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { PerStudentRecording?.StopAllAsync().GetAwaiter().GetResult(); } catch { }
        Telemetry?.Dispose();
        Beacon?.Dispose();
        PerStudentRecording?.Dispose();
        Recording?.Dispose();
        AdaptiveBitrate?.Dispose();
        StudentAudioMixer?.Dispose();
        AudioBroadcaster?.Dispose();
        ScreenBroadcaster?.Dispose();
        Server?.Dispose();
        // Phase 10.14 (Item 9) — clean up tray icon so it doesn't linger as a
        // ghost icon in the notification area after process exit.
        Notifier?.Dispose();
        base.OnExit(e);
    }

    private static void ApplyLocToResources()
    {
        var app = Current;
        if (app == null) return;
        foreach (var kv in Loc.EnumerateCurrent())
            app.Resources[kv.Key] = kv.Value;
    }

    private static void ApplyBrandingToResources()
    {
        var app = Current;
        if (app == null) return;

        // Phase 4.1 Hotfix — BrandingService.EnumerateResources() emits Primary-tinted
        // Surface.Background / .Elevated / .Overlay using Shade(primary, -0.85 / -0.75 / -0.65).
        // Those factors were sized for the pre-Phase-4 dark default and produce near-black
        // surfaces (e.g. #1E40AF → #04091A) on top of any baseline.  A direct App.Resources
        // entry beats a MergedDictionaries entry, so even though Colors.Light.xaml ships
        // #F8FAFC for Surface.Background, the runtime resolves to the near-black override.
        // When Light is active, skip those keys and explicitly Remove any prior write so the
        // Colors.Light.xaml values from MergedDictionaries flow through as the final answer.
        bool isLight = ClassroomCtrl.Shared.Branding.BrandingService.CurrentTheme
                     == ClassroomCtrl.Shared.Branding.ThemeMode.Light;

        // Phase 4.3 — BrandWallpaperBrush has a Shade(primary, -0.40) fallback for the
        // no-image case (designed for student lock-screen overlays where a tinted plain
        // brush is fine).  In MainWindow it would paint a dark navy backplane over the
        // Light theme, which isn't what App Background is supposed to do.  When the user
        // has no wallpaper file set, drop the key so the binding falls to Transparent and
        // the parent Surface.Background shows through.
        var cfg = ClassroomCtrl.Shared.Branding.BrandingService.Current;
        bool hasWallpaper = !string.IsNullOrWhiteSpace(cfg.WallpaperPath)
                         && System.IO.File.Exists(cfg.WallpaperPath);

        foreach (var kv in ClassroomCtrl.Shared.Branding.BrandingService.EnumerateResources())
        {
            if (isLight && IsBrandedDarkSurfaceKey(kv.Key))
            {
                app.Resources.Remove(kv.Key);
                continue;
            }
            if (kv.Key == "BrandWallpaperBrush" && !hasWallpaper)
            {
                app.Resources.Remove(kv.Key);
                continue;
            }
            app.Resources[kv.Key] = kv.Value;
        }
    }

    private static bool IsBrandedDarkSurfaceKey(string key)
        => key == "Surface.Background"
        || key == "Surface.Elevated"
        || key == "Surface.Overlay";

    private const string RegPath = @"Software\NTY\ClassroomCtrl";
    private const string RegValue = "Language";
    // Phase 11-B inc2 part 2 — opt-in HW H.264 encode toggle.  DWORD; non-zero = enabled.
    // Same key used by Student.Agent so one `reg add` controls both processes.
    // To enable on the dev box:
    //   reg add "HKCU\Software\NTY\ClassroomCtrl" /v UseHardwareH264 /t REG_DWORD /d 1 /f
    private const string RegValueHwH264 = "UseHardwareH264";

    /// <summary>Phase 11-B inc2 part 2 — opt-in flag read from HKCU.  Default false so
    /// production stays on the verified OpenH264 SW path until the dev's 2-PC test
    /// confirms MF HW output decodes on the existing OpenH264 decoder.  Read on demand
    /// (not cached) so a registry change takes effect the next time Share Screen is
    /// pressed without an app restart.</summary>
    public static bool UseHardwareH264
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath);
                return key?.GetValue(RegValueHwH264) is int i && i != 0;
            }
            catch { return false; }
        }
    }

    // Phase 11-B inc3 — opt-in DXGI Desktop Duplication capture toggle.  DWORD;
    // non-zero = enabled.  Same key the Student.Agent reads so one `reg add`
    // controls both processes.
    //   reg add "HKCU\Software\NTY\ClassroomCtrl" /v UseDxgiCapture /t REG_DWORD /d 1 /f
    private const string RegValueDxgi = "UseDxgiCapture";

    /// <summary>Phase 11-B inc3 — opt-in DXGI capture flag from HKCU.  inc4 flipped
    /// the default to <b>true</b> after the dev-box timing harness measured a 10×
    /// speedup vs GDI and the DxgiScreenCapturer's auto-fallback to GDI proved out
    /// (init failure, access-lost recovery, unsupported-RDP).  An absent key now
    /// means "use DXGI"; the dev/customer can still force GDI by setting the DWORD
    /// to 0 explicitly.  Read on demand (not cached) so a registry change takes
    /// effect the next time Share Screen is pressed without an app restart.</summary>
    public static bool UseDxgiCapture
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath);
                // Absent value → default true (inc4 flip).  Present + zero → false.
                return key?.GetValue(RegValueDxgi) is int i ? i != 0 : true;
            }
            catch { return true; }
        }
    }

    private static string? ReadPreferredLanguage()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegPath);
            return key?.GetValue(RegValue) as string;
        }
        catch { return null; }
    }

    public static void SavePreferredLanguage(string lang)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegPath);
            key.SetValue(RegValue, lang, RegistryValueKind.String);
        }
        catch { }
    }

    // Phase 5a debug aid: file logger at %TEMP%\teacher-debug.log
    // (Teacher.exe is WPF without an attached console, so AddConsole logs go nowhere.)
    private static readonly object _logLock = new();
    public static void LogDebug(string msg)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "teacher-debug.log");
            lock (_logLock)
            {
                System.IO.File.AppendAllText(path, $"[{System.DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
        }
        catch { }
    }
}