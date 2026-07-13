using Avalonia;

namespace ClassroomCtrl.Avalonia.Teacher;

class Program
{
    // Don't use Avalonia/third-party/SynchronizationContext-reliant code before
    // AppMain is called — the runtime isn't initialized yet.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Avalonia configuration; also used by the visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
