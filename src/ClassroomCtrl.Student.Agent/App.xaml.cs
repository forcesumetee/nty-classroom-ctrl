using ClassroomCtrl.Networking;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Student.Agent.Setup;
using ClassroomCtrl.Student.Agent.Tray;
using Microsoft.Win32;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;

namespace ClassroomCtrl.Student.Agent;

public partial class App : Application
{
    public static TrayIconManager? Tray { get; private set; }
    /// <summary>Phase 9 Section A — exposed so ScreenViewWindow's overlay can
    /// route mic toggle + chat send back to the MainWindow's existing handlers
    /// (single source of truth for audio + IPC envelope construction).</summary>
    public static MainWindow? MainWindowInstance { get; private set; }
    private TrayIconManager? _tray;
    private MainWindow? _mainWindow;
    private static Mutex? _singleInstanceMutex;
    private ScreenCapturer? _screenCapturer;

    public static IpcClient? Ipc { get; private set; }

    /// <summary>Phase 19 (v1.1): chat-embedded file-attachment persistence
    /// + open/download helpers.  Singleton shared between MainWindow's
    /// dispatch arms (receive path) + the chat-bubble UI (open / copy
    /// actions in the Conference sidebar template).</summary>
    public static ClassroomCtrl.Shared.Attachments.AttachmentManager? Attachments { get; private set; }

    /// <summary>Phase 21 (v1.1): student-side Conference screen-share
    /// broadcaster.  Singleton lifetime owned by the App so the capture
    /// loop survives Conference window close/reopen (matches the
    /// camera-broadcaster ownership model).  Start/Stop called from the
    /// shell VM's <c>ShareScreenCommand</c> state transitions.</summary>
    public static StudentConferenceShareBroadcaster? ConferenceShareBroadcaster { get; private set; }

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Belt-and-suspenders DPI awareness — see Teacher's App.xaml.cs for context.
        // The Agent captures the screen for student-screen-back streaming and renders the
        // teacher's broadcast in fullscreen, so it MUST see physical pixels at 125%/150% scaling.
        try { SetProcessDpiAwareness(2); }
        catch
        {
            try { SetProcessDPIAware(); } catch { /* old Windows — accept system DPI */ }
        }

        const string mutexName = @"Local\NTY_ClassroomAgent_Mutex";
        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        if (!createdNew) { Shutdown(); return; }

        // Localization
        var preferred = ReadPreferredLanguage();
        Loc.Initialize(preferred);
        Loc.LanguageChanged += ApplyLocToResources;
        ApplyLocToResources();

        // Phase 8 Section C — first-run TeacherIPDialog.  Pop the setup dialog
        // before IPC starts so the user-entered IP lands in config.txt before
        // the agent's first connection attempt.  Skip if the IP is already
        // configured (config.txt or legacy registry).
        if (!TeacherIPConfig.IsConfigured())
        {
            var dlg = new TeacherIPDialog();
            dlg.ShowDialog();
            // Even if user cancels, continue startup — the agent will idle
            // (no IP → no connection) and the next launch will re-prompt.
        }

        Ipc = new IpcClient();
        // Phase 19 (v1.1) — singleton attachment manager.  Order-insensitive
        // wrt Ipc/MainWindow init since SaveAsync is only ever called from
        // dispatch arms which fire after Ipc + MainWindow are up.
        Attachments = new ClassroomCtrl.Shared.Attachments.AttachmentManager();
        // Phase 21 (v1.1) — singleton screen-share broadcaster.  Idle until
        // the shell VM's Approved→Sharing transition fires Start.  No
        // capture thread runs while idle, so the cost of always-instantiating
        // is just one C# object + a few null fields.
        ConferenceShareBroadcaster = new StudentConferenceShareBroadcaster();
        Ipc.Start();

        _mainWindow = new MainWindow();
        MainWindowInstance = _mainWindow;
        _tray = new TrayIconManager(_mainWindow);
        _tray.Initialize();
        Tray = _tray;

        // Phase 11.3: branding live apply (mirrors Loc).
        // Phase 8 — Student.Agent forces Light at startup (matching Teacher's
        // Phase 4 D fix), so an old branding.json that still says "Dark"
        // doesn't drag the agent back to the dark surface.  Customer request:
        // Student.Agent should look identical in tone to the Teacher app.
        ClassroomCtrl.Shared.Branding.BrandingService.Initialize();
        ClassroomCtrl.Shared.Branding.BrandingService.ApplyTheme(
            ClassroomCtrl.Shared.Branding.ThemeMode.Light);
        ClassroomCtrl.Shared.Branding.BrandingService.Changed += ApplyBrandingToResources;
        ApplyBrandingToResources();

        _screenCapturer = new ScreenCapturer();
        _screenCapturer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Phase 12-B — best-effort release of every modifier + mouse button.
        // If the Agent is killed mid-remote-control, the student's OS would
        // otherwise be left with held state; ReleaseAll is idempotent and
        // harmless if no remote-control session was active.
        try { RemoteControlReceiver.ReleaseAll(); } catch { }

        _screenCapturer?.Stop();
        // Phase 21 (v1.1) — best-effort stop of an active share before the
        // pipe closes so the teacher gets a real ConferenceShareStop
        // envelope instead of inferring shutdown from the disconnect.
        try { ConferenceShareBroadcaster?.Dispose(); } catch { }
        Ipc?.Stop();
        _tray?.Dispose();

        // OnExit can run on a thread that didn't acquire the mutex (WPF dispatcher
        // teardown can happen from finalizer / shutdown threads). ReleaseMutex throws
        // ApplicationException in that case — Dispose alone is sufficient to release
        // the OS handle and the kernel mutex on process exit.
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { /* not owner — Dispose will clean up */ }
            catch (ObjectDisposedException) { /* already disposed */ }
            catch (InvalidOperationException) { /* not held */ }

            try { _singleInstanceMutex.Dispose(); }
            catch { }
            _singleInstanceMutex = null;
        }

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
        // Phase 8 — port of Teacher's Phase 4.1 hotfix.  BrandingService still
        // emits Surface.Background / Surface.Elevated / Surface.Overlay tinted
        // for a dark theme; with the Agent now force-Light those keys would
        // win over Colors.Light.xaml in the merged dictionary chain.  Strip
        // them on Light, and skip BrandWallpaperBrush when no wallpaper file
        // exists so DynamicResource falls back to Transparent.
        var app = Current;
        if (app == null) return;
        bool isLight = ClassroomCtrl.Shared.Branding.BrandingService.CurrentTheme
                     == ClassroomCtrl.Shared.Branding.ThemeMode.Light;
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
        => key == "Surface.Background" || key == "Surface.Elevated" || key == "Surface.Overlay";

    private const string RegPath = @"Software\NTY\ClassroomCtrl";
    private const string RegValue = "Language";
    // Phase 11-B inc2 part 2 — opt-in HW H.264 encode toggle.  DWORD; non-zero = enabled.
    // Same key the Teacher reads so one `reg add` controls both processes.
    private const string RegValueHwH264 = "UseHardwareH264";

    /// <summary>Phase 11-B inc2 part 2 — opt-in flag read from HKCU.  Default false so
    /// production stays on the verified OpenH264 SW path.  Read on demand so a registry
    /// change takes effect at the next StudentBroadcaster Start without an app restart.</summary>
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

    // Phase 11-B inc3 — opt-in DXGI Desktop Duplication capture toggle.  Same
    // key the Teacher reads so one `reg add` controls both processes.
    //   reg add "HKCU\Software\NTY\ClassroomCtrl" /v UseDxgiCapture /t REG_DWORD /d 1 /f
    private const string RegValueDxgi = "UseDxgiCapture";

    /// <summary>Phase 11-B inc3 — opt-in DXGI capture flag from HKCU.  inc4 flipped
    /// the default to <b>true</b>: absent key means "use DXGI", explicit DWORD=0
    /// forces GDI.  Capturer auto-falls-back to GDI on any DXGI failure so the
    /// student broadcaster never blanks regardless of the flag.</summary>
    public static bool UseDxgiCapture
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RegPath);
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
}