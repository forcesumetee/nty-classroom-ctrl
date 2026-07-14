using Avalonia;

namespace ClassroomCtrl.Avalonia.Teacher;

class Program
{
    // Don't use Avalonia/third-party/SynchronizationContext-reliant code before
    // AppMain is called — the runtime isn't initialized yet.
    [STAThread]
    public static int Main(string[] args)
    {
        // TT-13 (TOR 11.2.9) record gate — runs headlessly INSIDE the granted bundle (SCK recording
        // needs Screen Recording TCC = a bundle identity). Set NTY_RECORD_SELFTEST=<seconds> and launch
        // the .app's apphost: it records, stops, and PASSes iff the file has BOTH video and audio
        // samples and is non-zero. No UI (returns before the Avalonia lifetime starts).
        var sel = Environment.GetEnvironmentVariable("NTY_RECORD_SELFTEST");
        if (!string.IsNullOrEmpty(sel))
            return RecordSelfTest(int.TryParse(sel, out var s) ? s : 10);

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static int RecordSelfTest(int seconds)
    {
        var rec = new Services.TeacherRecorder();
        var (ok, msg) = rec.Start();
        Console.WriteLine($"[record-selftest] start ok={ok} — {msg}");
        if (!ok) { Console.WriteLine("RECORD SELFTEST FAIL ❌ (start failed — grant Screen Recording + RELAUNCH?)"); return 1; }
        System.Threading.Thread.Sleep(seconds * 1000);
        var (sok, path, video, audio) = rec.Stop();
        long size = (path != null && System.IO.File.Exists(path)) ? new System.IO.FileInfo(path).Length : 0;
        Console.WriteLine($"[record-selftest] stop ok={sok} path={path}");
        Console.WriteLine($"[record-selftest] video frames={video}  audio frames={audio}  file size={size:N0} bytes");
        bool pass = sok && video > 0 && audio > 0 && size > 0;
        Console.WriteLine(pass
            ? "RECORD SELFTEST PASS ✅ — .mov written with BOTH video and audio tracks"
            : "RECORD SELFTEST FAIL ❌");
        return pass ? 0 : 1;
    }

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
