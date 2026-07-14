using ClassroomCtrl.Student.Mac;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// ────────────────────────────────────────────────────────────────────────
//  NTY ClassroomCtrl — macOS Student Daemon
//
//  Usage:
//    dotnet run                          # run in foreground (development)
//    ./ClassroomCtrl.Student.Mac         # run as a launchd agent/daemon
//
//  The daemon listens on a Unix Domain Socket for Agent (UI) connections
//  and routes Teacher commands to native macOS handlers.
// ────────────────────────────────────────────────────────────────────────

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.SetMinimumLevel(LogLevel.Debug);
builder.Logging.AddSimpleConsole(opts =>
{
    opts.IncludeScopes = false;
    opts.TimestampFormat = "HH:mm:ss.fff ";
});

// Services
builder.Services.AddSingleton<LockService>();
builder.Services.AddSingleton<MacPolicyEnforcer>();
builder.Services.AddHostedService<MacClassroomWorker>();

var app = builder.Build();
app.Run();
