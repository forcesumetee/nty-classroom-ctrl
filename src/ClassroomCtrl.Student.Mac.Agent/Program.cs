using System;
using Avalonia;

namespace ClassroomCtrl.Student.Mac.Agent;

// ────────────────────────────────────────────────────────────────────────
//  NTY ClassroomCtrl — macOS Student Agent (tray UI)
//
//  A thin menubar companion to the headless daemon (ClassroomCtrl.Student.Mac).
//  It owns NO teacher connection and no enforcement — it connects to the
//  daemon's Unix domain socket, reflects status, and toasts notifications
//  (e.g. a teacher file arriving). All the guaranteed work lives in the daemon.
// ────────────────────────────────────────────────────────────────────────

internal static class Program
{
    // Don't use any Avalonia, third-party APIs or SynchronizationContext-reliant
    // code before AppMain is called: things aren't initialized yet.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration, don't remove; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
