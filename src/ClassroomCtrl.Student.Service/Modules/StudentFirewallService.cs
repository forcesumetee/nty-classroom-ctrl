using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;

namespace ClassroomCtrl.Student.Service.Modules;

/// <summary>
/// Phase 10.10 Fix 4 — Student-side firewall rules.  Until now only the Teacher
/// app added inbound rules for itself; on a fresh Windows 11 install with the
/// network adapter on the Public profile, UDP 7778 (discovery beacons from the
/// teacher) is silently dropped before the Service ever sees it.  Result: no
/// auto-discovery, students wait forever on the discovery loop.
///
/// Idempotent — checks rule presence via netsh first; only invokes the elevated
/// add path when missing.  Service runs as LocalSystem (or via Phase 10.9 task
/// scheduler running with HighestAvailable in user session), so no UAC prompt
/// is required.
///
/// Two rules are managed:
///   * UDP 7778 inbound — required to receive the teacher's discovery beacon.
///   * TCP 7777 outbound — usually permitted by default Windows policy, but
///     locked-down enterprise deployments occasionally restrict it.  Adding
///     an explicit allow makes the configuration self-documenting and survives
///     group-policy refreshes that strip the implicit allow.
/// </summary>
internal static class StudentFirewallService
{
    private const string UdpInRuleName = "ClassroomCtrl Student UDP 7778 (auto)";
    private const string TcpOutRuleName = "ClassroomCtrl Student TCP 7777 (auto)";
    private static readonly int UdpInPort = NetworkConstants.DiscoveryUdpPort;
    private static readonly int TcpOutPort = NetworkConstants.ControlTcpPort;

    public static void EnsureRules(ILogger logger)
    {
        try
        {
            var udpOk = RuleExists(UdpInRuleName, logger);
            var tcpOk = RuleExists(TcpOutRuleName, logger);
            if (udpOk && tcpOk) return;

            if (!udpOk)
            {
                AddRule(UdpInRuleName,
                    $"dir=in action=allow protocol=UDP localport={UdpInPort} profile=any",
                    logger);
            }
            if (!tcpOk)
            {
                AddRule(TcpOutRuleName,
                    $"dir=out action=allow protocol=TCP remoteport={TcpOutPort} profile=any",
                    logger);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "StudentFirewallService.EnsureRules failed");
        }
    }

    private static bool RuleExists(string name, ILogger logger)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{name}\"")
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
            return output.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0
                && output.IndexOf("No rules match", StringComparison.OrdinalIgnoreCase) < 0;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "RuleExists check failed for {Name}", name);
            return false;
        }
    }

    private static void AddRule(string name, string ruleSpec, ILogger logger)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh",
                $"advfirewall firewall add rule name=\"{name}\" {ruleSpec}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                logger.LogWarning("AddRule: netsh failed to start (rule={Name})", name);
                return;
            }
            if (!proc.WaitForExit(5000))
            {
                try { proc.Kill(); } catch { }
                logger.LogWarning("AddRule: netsh timed out (rule={Name})", name);
                return;
            }
            if (proc.ExitCode == 0)
            {
                logger.LogInformation("Firewall rule added: {Name}", name);
            }
            else
            {
                var stderr = proc.StandardError.ReadToEnd();
                logger.LogWarning("AddRule: netsh exit={Code} (rule={Name}): {Err}",
                    proc.ExitCode, name, stderr);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AddRule failed for {Name}", name);
        }
    }
}
