using ClassroomCtrl.Shared.Telemetry;
using Microsoft.Win32;
using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 1: Counters in-process, persisted to telemetry.json every 60 sec.
/// Offline only — never sends over the network.
/// </summary>
public class TelemetryService : IDisposable
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NTY", "ClassroomCtrl");
    private static readonly string PersistPath = Path.Combine(Folder, "telemetry.json");

    private readonly TelemetryPayload _data;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly Timer _persistTimer;
    private readonly object _lock = new();

    public TelemetryService()
    {
        try { Directory.CreateDirectory(Folder); } catch { }
        _data = LoadOrCreate();
        _data.SessionsCount++;
        _data.AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "";
        _data.OsVersion = Environment.OSVersion.VersionString;
        _data.LastSeenUtc = DateTime.UtcNow;
        Persist();

        _persistTimer = new Timer(_ => { try { Persist(); } catch { } },
            null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public void RecordError(string message)
    {
        lock (_lock)
        {
            _data.ErrorCount++;
            _data.LastErrorMessage = message ?? "";
        }
    }

    private void Persist()
    {
        lock (_lock)
        {
            _data.Uptime = DateTime.UtcNow - _startedAt;
            _data.LastSeenUtc = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            try { File.WriteAllText(PersistPath, json); } catch { }
        }
    }

    private static TelemetryPayload LoadOrCreate()
    {
        try
        {
            if (File.Exists(PersistPath))
            {
                var json = File.ReadAllText(PersistPath);
                var p = JsonSerializer.Deserialize<TelemetryPayload>(json);
                if (p != null && !string.IsNullOrEmpty(p.InstallId)) return p;
            }
        }
        catch { }

        return new TelemetryPayload
        {
            InstallId = ReadOrCreateInstallId(),
            MachineHash = HashMachine(),
        };
    }

    private static string ReadOrCreateInstallId()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\NTY\ClassroomCtrl");
            var existing = key?.GetValue("InstallId") as string;
            if (!string.IsNullOrEmpty(existing)) return existing!;
            var newId = Guid.NewGuid().ToString("N");
            key?.SetValue("InstallId", newId);
            return newId;
        }
        catch { return Guid.NewGuid().ToString("N"); }
    }

    private static string HashMachine()
    {
        var raw = Environment.MachineName + "|" + Environment.UserName;
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(64);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public void Dispose()
    {
        _persistTimer.Dispose();
        Persist();
    }
}
