using System;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 14: Append-only audit log. Each call writes one JSON line to
/// %ProgramData%\NTY\ClassroomCtrl\Audit\audit-{yyyy-MM-dd}.jsonl. Best-effort
/// — never throws, never blocks the caller.
/// </summary>
public class AuditLogService
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NTY", "ClassroomCtrl", "Audit");

    private readonly object _lock = new();

    public AuditLogService()
    {
        try { Directory.CreateDirectory(Folder); } catch { }
    }

    public void Log(string action, object? detail = null)
    {
        try
        {
            var entry = new
            {
                Ts = DateTimeOffset.UtcNow.ToString("o"),
                User = Environment.UserName,
                Machine = Environment.MachineName,
                Action = action,
                Detail = detail,
            };
            var line = JsonSerializer.Serialize(entry);
            var path = Path.Combine(Folder, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            lock (_lock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch { /* never let audit failure crash caller */ }
    }
}
