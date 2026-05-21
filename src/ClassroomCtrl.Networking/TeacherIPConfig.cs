using ClassroomCtrl.Shared.Protocol;
using Microsoft.Win32;
using System;
using System.IO;
using System.Net;

namespace ClassroomCtrl.Networking;

/// <summary>
/// Reads/writes the configured Teacher IP.
///
/// Phase 8 Section B — switched primary storage from HKLM registry to a
/// plain-text config file at %ProgramData%\NTY\ClassroomCtrl\config.txt.
///
/// Why: writing to HKLM requires administrator elevation, which fails on
/// typical school student PCs (e.g. when the IT lab tech is using AnyDesk
/// without UAC elevation, the Setup dialog's Save would throw
/// UnauthorizedAccessException).  %ProgramData% is writable by any logged-in
/// user (or by NetworkService for the Student.Service process), so the
/// first-run TeacherIPDialog now succeeds without elevation.
///
/// Backward compatibility: Read() falls back to the legacy registry value
/// when config.txt is absent so older deployments keep working until they
/// re-run the IPDialog.  The Service (which we cannot modify in this
/// phase) still calls GetEndpoint() / IsConfigured() through this class —
/// those keep working unchanged.
/// </summary>
public static class TeacherIPConfig
{
    private const string RegistryPath = @"Software\NTY\ClassroomCtrl";
    private const string RegistryValueName = "TeacherIP";

    /// <summary>%ProgramData%\NTY\ClassroomCtrl\config.txt — readable + writable
    /// by any user without elevation.  Same folder used by ClassRosterService,
    /// BrandingService, Recordings, etc.</summary>
    public static string ConfigFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NTY", "ClassroomCtrl", "config.txt");

    /// <summary>Returns the saved IP, or null if not configured yet.
    /// Tries the file first, then falls back to the legacy HKLM value so
    /// existing deployments don't break before they've re-saved via the
    /// new dialog.</summary>
    public static string? Read()
    {
        // Primary: config.txt
        try
        {
            if (File.Exists(ConfigFilePath))
            {
                var contents = File.ReadAllText(ConfigFilePath).Trim();
                if (!string.IsNullOrEmpty(contents)) return contents;
            }
        }
        catch { /* fall through to registry */ }

        // Legacy fallback: registry.  Only HKLM (machine-wide) was ever used
        // here; HKCU was never written by Write().  Returning a registry
        // value still satisfies Service callers during the migration window.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            return key?.GetValue(RegistryValueName) as string;
        }
        catch { return null; }
    }

    /// <summary>Saves the IP to config.txt.  Creates the parent directory
    /// if missing.  No admin elevation required.  After a successful write,
    /// best-effort delete the legacy registry value so that subsequent Read()
    /// calls can't be confused by a stale machine-wide entry that disagrees
    /// with the file.</summary>
    public static void Write(string ipAddress)
    {
        var dir = Path.GetDirectoryName(ConfigFilePath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ConfigFilePath, ipAddress.Trim());

        // Best-effort registry cleanup — silently ignored if not admin.
        // Leaving stale registry around is harmless because Read() prefers
        // the file; we just clean up so deployments age out cleanly.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath, writable: true);
            if (key?.GetValue(RegistryValueName) != null)
                key.DeleteValue(RegistryValueName, throwOnMissingValue: false);
        }
        catch { /* not admin; the registry value lingers but Read() ignores it */ }
    }

    /// <summary>
    /// Resolve the configured Teacher endpoint.  Accepts both "IP" (default port from
    /// <see cref="NetworkConstants.ControlTcpPort"/>) and "IP:port" formats.
    ///
    /// Phase 10.14 hotfix — added IPEndPoint.TryParse fallback because the Setup
    /// dialog only accepts plain IP, but a hand-edited config.txt with "IP:port"
    /// used to silently fall back to loopback while IsConfigured() reported true.
    /// Better to honor what the user actually wrote, or fail loudly via
    /// IsConfigured()=false (callers then re-prompt the dialog or fall back to
    /// UDP beacon discovery — both strictly better than connecting to 127.0.0.1).
    /// </summary>
    public static IPEndPoint GetEndpoint()
    {
        var raw = Read()?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return new IPEndPoint(IPAddress.Loopback, NetworkConstants.ControlTcpPort);

        // Common case: plain "192.168.1.153" — use default port.
        if (IPAddress.TryParse(raw, out var ip))
            return new IPEndPoint(ip, NetworkConstants.ControlTcpPort);

        // Defensive: "192.168.1.153:7777" — honor the port explicitly.
        if (IPEndPoint.TryParse(raw, out var endpoint))
            return endpoint;

        // Garbage in config.txt — fall back to loopback; IsConfigured() returns
        // false below so the caller knows the config is broken.
        return new IPEndPoint(IPAddress.Loopback, NetworkConstants.ControlTcpPort);
    }

    /// <summary>
    /// Phase 10.14 hotfix — true only when the configured value parses as a valid
    /// IP or IP:port.  Previously this returned true for any non-empty string,
    /// which caused a misleading "configured: true" log line even when GetEndpoint()
    /// silently fell back to 127.0.0.1.
    /// </summary>
    public static bool IsConfigured()
    {
        var raw = Read()?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return false;
        return IPAddress.TryParse(raw, out _) || IPEndPoint.TryParse(raw, out _);
    }
}
