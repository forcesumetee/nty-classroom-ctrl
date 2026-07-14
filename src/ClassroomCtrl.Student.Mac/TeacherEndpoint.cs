using System.Net;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Student.Mac;

/// <summary>
/// Resolves where the daemon should dial the Teacher, and the student display name it announces.
///
/// Sources, in priority order:
///   1. Environment: <c>NTY_TEACHER_IP</c> (+ optional <c>NTY_TEACHER_PORT</c>) — handy for dev / launchd.
///   2. Config file: <c>~/Library/Application Support/NTY/ClassroomCtrl/teacher.txt</c>, one line
///      <c>ip</c> or <c>ip:port</c> (the tray Agent will write this once a "connect to teacher" UX exists).
/// Port defaults to <see cref="NetworkConstants.ControlTcpPort"/> (7777). Returns false when no teacher
/// is configured yet — the worker then idles the TCP loop instead of busy-dialing nothing.
/// </summary>
public static class TeacherEndpoint
{
    private static string ConfigFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "NTY", "ClassroomCtrl", "teacher.txt");

    public static bool TryResolve(out string ip, out int port)
    {
        ip = "";
        port = NetworkConstants.ControlTcpPort;

        var raw = Environment.GetEnvironmentVariable("NTY_TEACHER_IP");
        if (!string.IsNullOrWhiteSpace(raw))
        {
            var envPort = Environment.GetEnvironmentVariable("NTY_TEACHER_PORT");
            if (int.TryParse(envPort, out var p) && p is > 0 and <= 65535) port = p;
            return Parse(raw, ref ip, ref port);
        }

        try
        {
            if (File.Exists(ConfigFile))
            {
                var line = File.ReadAllText(ConfigFile).Trim();
                if (line.Length > 0) return Parse(line, ref ip, ref port);
            }
        }
        catch { /* unreadable config → treat as unconfigured */ }

        return false;
    }

    /// <summary>Student display name shown on the Teacher's tile. <c>NTY_STUDENT_NAME</c> overrides the machine name.</summary>
    public static string DisplayName()
    {
        var name = Environment.GetEnvironmentVariable("NTY_STUDENT_NAME");
        return string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
    }

    private static bool Parse(string value, ref string ip, ref int port)
    {
        var s = value.Trim();
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out var p) && p is > 0 and <= 65535)
        {
            port = p;
            s = s[..colon];
        }
        if (IPAddress.TryParse(s, out _)) { ip = s; return true; }
        return false;
    }
}
