using System;
using Microsoft.Extensions.Logging;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-2-D — a tiny console ILogger (no logging package → offline-safe) so the
/// in-app ControlServer's connect / join / stale-sweep / disconnect logs are
/// visible in the terminal when the Teacher app is run via `dotnet run`. Mirrors
/// the one in tools/TeacherHost.
/// </summary>
public sealed class ConsoleLoggerFactory : ILoggerFactory
{
    public ILogger CreateLogger(string categoryName) => new ConsoleLogger(categoryName);
    public void AddProvider(ILoggerProvider provider) { }
    public void Dispose() { }
}

internal sealed class ConsoleLogger : ILogger
{
    private readonly string _cat;
    public ConsoleLogger(string cat) { int dot = cat.LastIndexOf('.'); _cat = dot >= 0 ? cat[(dot + 1)..] : cat; }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> fmt)
    {
        if (!IsEnabled(level)) return;
        string tag = level switch { LogLevel.Warning => "WARN", LogLevel.Error or LogLevel.Critical => "ERR ", _ => "info" };
        Console.WriteLine($"    [{tag}] {_cat}: {fmt(state, ex)}");
        if (ex is not null) Console.WriteLine($"           {ex.GetType().Name}: {ex.Message}");
    }
}
