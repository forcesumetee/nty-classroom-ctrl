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
        _screenCapturer?.Stop();
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