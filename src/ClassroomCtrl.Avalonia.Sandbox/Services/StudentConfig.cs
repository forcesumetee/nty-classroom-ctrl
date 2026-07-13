using System;
using System.IO;
using System.Text.Json;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// Phase 32-B — persistent student config, mirroring the shipped Windows file model
/// (the Windows student deliberately moved Teacher IP OFF the registry into an
/// unprivileged, admin-pre-seedable file so it works without elevation). Stored as JSON at
/// <c>~/Library/Application Support/NTY ClassroomCtrl/config.json</c>
/// (<see cref="Environment.SpecialFolder.ApplicationData"/> resolves there on macOS — verified).
///
/// Contract: load at startup, save on change (commit). Missing or corrupt file → sensible
/// defaults, NEVER throws. Admin pre-seed = drop this file (or push via MDM/profile); student
/// edits it at runtime by connecting. An optional <c>dir</c> override keeps tests off the real path.
///
/// Example (admin pre-seed):
/// <code>
/// { "teacherIp": "172.20.10.7", "port": 7777, "displayName": "LAB-01", "channelId": "1234" }
/// </code>
/// </summary>
public sealed class StudentConfig
{
    public string TeacherIp { get; set; } = "";
    public int Port { get; set; } = 7777;
    public string DisplayName { get; set; } = DefaultDisplayName();
    public string ChannelId { get; set; } = "1234";

    /// <summary>Default display name = the Mac's host name (mirrors the Windows student's
    /// <c>Environment.MachineName</c> broadcast).</summary>
    public static string DefaultDisplayName() => Environment.MachineName;

    private const string AppFolderName = "NTY ClassroomCtrl";
    private const string FileName = "config.json";

    /// <summary>The production directory: <c>~/Library/Application Support/NTY ClassroomCtrl</c>.</summary>
    public static string DefaultDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,   // tolerate an admin hand-editing the file with any casing
    };

    /// <summary>Load config from <paramref name="dir"/> (null → <see cref="DefaultDir"/>). Missing or
    /// unreadable/corrupt → normalized defaults; never throws. <paramref name="log"/> gets one diagnostic line.</summary>
    public static StudentConfig Load(string? dir = null, Action<string>? log = null)
    {
        var path = Path.Combine(dir ?? DefaultDir, FileName);
        try
        {
            if (!File.Exists(path))
            {
                log?.Invoke($"config: none at {path} — using defaults (name={DefaultDisplayName()})");
                return Normalized(new StudentConfig());
            }
            var cfg = JsonSerializer.Deserialize<StudentConfig>(File.ReadAllText(path), JsonOpts);
            if (cfg is null)
            {
                log?.Invoke("config: file was empty/null — using defaults");
                return Normalized(new StudentConfig());
            }
            log?.Invoke($"config: loaded {path}");
            return Normalized(cfg);
        }
        catch (Exception ex)
        {
            // Corrupt JSON, permission error, etc. — degrade to defaults rather than crash the app.
            log?.Invoke($"config: unreadable ({ex.GetType().Name}: {ex.Message}) — using defaults");
            return Normalized(new StudentConfig());
        }
    }

    /// <summary>Save to <paramref name="dir"/> (null → <see cref="DefaultDir"/>), creating the folder.
    /// Best-effort — never throws; logs on failure.</summary>
    public void Save(string? dir = null, Action<string>? log = null)
    {
        var d = dir ?? DefaultDir;
        var path = Path.Combine(d, FileName);
        try
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(path, JsonSerializer.Serialize(Normalized(this), JsonOpts));
            log?.Invoke($"config: saved {path}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"config: save failed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    /// <summary>Fill blanks / clamp so callers never see an empty display name or an out-of-range port,
    /// however the file was seeded. TeacherIp may legitimately stay empty (not yet configured).</summary>
    private static StudentConfig Normalized(StudentConfig c)
    {
        c.TeacherIp = (c.TeacherIp ?? "").Trim();
        c.DisplayName = string.IsNullOrWhiteSpace(c.DisplayName) ? DefaultDisplayName() : c.DisplayName.Trim();
        c.ChannelId = string.IsNullOrWhiteSpace(c.ChannelId) ? "1234" : c.ChannelId.Trim();
        if (c.Port is < 1 or > 65535) c.Port = 7777;
        return c;
    }
}
