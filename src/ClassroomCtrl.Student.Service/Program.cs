using ClassroomCtrl.Student.Service;
using ClassroomCtrl.Student.Service.Modules;
using Serilog;

var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                           "ClassroomCtrl", "logs", "service-.log");
Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(logPath, rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
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
    builder.Services.AddSingleton<ProcessSupervisor>();
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
