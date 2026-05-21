using ClassroomCtrl.Student.Service;
using ClassroomCtrl.Student.Service.Modules;
using Serilog;

// Phase 10.8 — log path moved under NTY\ to match other product data
// (TeacherIPConfig.config.txt at NTY\ClassroomCtrl\config.txt from Phase 8 B)
// so all customer-facing artifacts live under one folder.  Both SYSTEM
// (Service mode, Session 0) and the interactive user resolve
// SpecialFolder.CommonApplicationData to %PROGRAMDATA% identically, so the
// path is stable across the console-mode vs Service-mode regressions
// Phase 10.8 is investigating.
var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                           "NTY", "ClassroomCtrl", "logs", "service-.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    // Phase 10.14 (Item 7) — explicit UTF-8 so Thai characters and em-dashes
    // (— used throughout our log messages) don't render as `โ€` / `เน€เธ”`
    // when opened from a Thai-locale Windows console or text editor that
    // default-guesses cp874 / Windows-1252.
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 7,
        encoding: System.Text.Encoding.UTF8)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Only register as Windows Service when actually running under SCM (production).
    // In dev console mode, this is skipped so the host stays alive.
    if (Environment.UserInteractive == false || args.Contains("--service"))
    {
        builder.Services.AddWindowsService(options => { options.ServiceName = "ClassroomService"; });
    }

    builder.Services.AddSerilog();

    builder.Services.AddSingleton<IpcServer>();
    builder.Services.AddSingleton<PolicyEnforcer>();
    builder.Services.AddSingleton<ScreenLocker>();
    builder.Services.AddSingleton<FileReceiver>(); 
    builder.Services.AddHostedService<ClassroomWorker>();

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Service terminated unexpectedly");
}
finally { Log.CloseAndFlush(); }
