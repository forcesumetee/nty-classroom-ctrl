using ClassroomCtrl.Shared.Protocol;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 9.1 Section A — auto-add Windows Firewall inbound rules for the Control
/// TCP port and the discovery UDP port, so that on a fresh install (especially
/// when the network adapter is in the Public profile) students can connect
/// without an IT tech manually running PowerShell.
///
/// Idempotent: <see cref="EnsureRules"/> first checks via netsh whether the
/// rules already exist; only if either is missing does it spawn a UAC-elevated
/// netsh invocation.  Subsequent app launches see both rules and return
/// silently.  If the user dismisses the UAC prompt, we swallow the
/// <see cref="Win32Exception"/> and leave the rule absent — connection will
/// fail and the user can re-launch later to retry.
///
/// Ports come from <see cref="NetworkConstants"/> so a future port change in
/// Shared/Protocol/Constants.cs propagates automatically without touching this
/// file.  MediaRtpPortBase (7779) and FileMulticastPort (7780) are not opened
/// here — those are outbound on the Teacher side or scoped to the multicast
/// group; if a deployment ever needs them as inbound listeners, add them in
/// the same pattern below.
/// </summary>
public static class FirewallService
{
    private const string TcpRuleName = "ClassroomCtrl Teacher TCP (auto)";
    private const string UdpRuleName = "ClassroomCtrl Teacher UDP (auto)";
    private static readonly int TcpPort = NetworkConstants.ControlTcpPort;
    private static readonly int UdpPort = NetworkConstants.DiscoveryUdpPort;

    /// <summary>Idempotent — safe to call from OnStartup on every launch.</summary>
    public static void EnsureRules()
    {
        try
        {
            if (RuleExists(TcpRuleName) && RuleExists(UdpRuleName)) return;
            AddRulesElevated();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FirewallService] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool RuleExists(string name)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh",
                $"advfirewall firewall show rule name=\"{name}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            var output = proc.StandardOutput.ReadToEnd();
            // netsh prints "No rules match the specified criteria" when absent.
            return output.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                && output.IndexOf("No rules match", StringComparison.OrdinalIgnoreCase) < 0;
        }
        catch
        {
            return false;
        }
    }

    private static void AddRulesElevated()
    {
        // Wrap the two netsh calls in a temporary .bat so a single UAC prompt
        // covers both rule additions.  Direct ProcessStart with Verb=runas on
        // netsh.exe would prompt twice (once per rule).
        var batchPath = Path.Combine(Path.GetTempPath(), "classroomctrl_fw.bat");
        File.WriteAllText(batchPath,
            "@echo off\r\n" +
            $"netsh advfirewall firewall add rule name=\"{TcpRuleName}\" " +
            $"dir=in action=allow protocol=TCP localport={TcpPort} profile=any\r\n" +
            $"netsh advfirewall firewall add rule name=\"{UdpRuleName}\" " +
            $"dir=in action=allow protocol=UDP localport={UdpPort} profile=any\r\n");
        try
        {
            var psi = new ProcessStartInfo(batchPath)
            {
                Verb = "runas",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch (Win32Exception)
        {
            // User dismissed the UAC prompt.  Leave rules absent; next app
            // launch will re-prompt.  Document in chat-system message?  No —
            // EnsureRules is fire-and-forget; the user will discover via
            // students unable to connect, then re-launch to accept UAC.
        }
        finally
        {
            try { File.Delete(batchPath); } catch { /* leave temp behind on
                error; OS cleans %TEMP% eventually */ }
        }
    }
}
