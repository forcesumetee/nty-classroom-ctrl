using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Student.Agent.Tray;
using Microsoft.Win32;
using System.Threading;
using System.Windows;
using Application = System.Windows.Application;

namespace ClassroomCtrl.Student.Agent;

public partial class App : Application
{
    public static TrayIconManager? Tray { get; private set; }
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

        AllocConsole();

        const string mutexName = @"Local\NTY_ClassroomAgent_Mutex";
        bool createdNew;
        _singleInstanceMutex = new Mutex(initiallyOwned: true, mutexName, out createdNew);
        if (!createdNew) { Shutdown(); return; }

        // Localization
        var preferred = ReadPreferredLanguage();
        Loc.Initialize(preferred);
        Loc.LanguageChanged += ApplyLocToResources;
        ApplyLocToResources();

        Ipc = new IpcClient();
        Ipc.Start();

        _mainWindow = new MainWindow();
        _tray = new TrayIconManager(_mainWindow);
        _tray.Initialize();
        Tray = _tray;

        // Phase 11.3: branding live apply (mirrors Loc).
        // Phase 6C: also restores the persisted Light/Dark theme before brand colors land.
        ClassroomCtrl.Shared.Branding.BrandingService.Initialize();
        ClassroomCtrl.Shared.Branding.BrandingService.ApplyTheme(
            ClassroomCtrl.Shared.Branding.BrandingService.CurrentTheme);
        ClassroomCtrl.Shared.Branding.BrandingService.Changed += ApplyBrandingToResources;
        ApplyBrandingToResources();

        _screenCapturer = new ScreenCapturer();
        _screenCapturer.Start();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

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
        var app = Current;
        if (app == null) return;
        foreach (var kv in ClassroomCtrl.Shared.Branding.BrandingService.EnumerateResources())
            app.Resources[kv.Key] = kv.Value;
    }

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