using ClassroomCtrl.Shared.Protocol;
using Microsoft.Win32;
using System.Net;

namespace ClassroomCtrl.Networking;

/// <summary>
/// Reads/writes the configured Teacher IP from the registry (Spec §5.1).
/// Path: HKLM\Software\NTY\ClassroomCtrl\TeacherIP
/// Writing requires administrator. Reading is fine for any user.
/// </summary>
public static class TeacherIPConfig
{
    private const string RegistryPath = @"Software\NTY\ClassroomCtrl";
    private const string RegistryValueName = "TeacherIP";

    /// <summary>Returns the saved IP, or null if not configured yet.</summary>
    public static string? Read()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryPath);
            return key?.GetValue(RegistryValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Saves the IP to HKLM. Throws if not running as admin.</summary>
    public static void Write(string ipAddress)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryPath, writable: true);
        key.SetValue(RegistryValueName, ipAddress, RegistryValueKind.String);
    }

    /// <summary>Returns the configured Teacher endpoint, or 127.0.0.1 fallback for dev.</summary>
    public static IPEndPoint GetEndpoint()
    {
        var ipString = Read();
        if (!string.IsNullOrWhiteSpace(ipString) && IPAddress.TryParse(ipString, out var ip))
        {
            return new IPEndPoint(ip, NetworkConstants.ControlTcpPort);
        }
        return new IPEndPoint(IPAddress.Loopback, NetworkConstants.ControlTcpPort);
    }

    public static bool IsConfigured() => !string.IsNullOrWhiteSpace(Read());
}