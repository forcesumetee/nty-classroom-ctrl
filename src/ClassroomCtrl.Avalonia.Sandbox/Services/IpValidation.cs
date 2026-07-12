using System.Net;
using System.Text.RegularExpressions;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// Shared IPv4 validation, extracted from the Phase 25.7-C TeacherIPDialog port so
/// the connection flow (Phase 26.0) reuses the exact same rule. Belt-and-suspenders:
/// a dotted-quad Regex AND <see cref="IPAddress.TryParse"/> — TryParse alone
/// permissively accepts "10.0.0" and octal "0700.0.0.1", so the regex pins the
/// canonical four-decimal-octet form.
/// </summary>
public static class IpValidation
{
    private static readonly Regex Ipv4Regex = new(
        @"^(?:(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(?:25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$",
        RegexOptions.Compiled);

    public static bool IsValidIpv4(string? text)
    {
        var t = (text ?? "").Trim();
        return Ipv4Regex.IsMatch(t) && IPAddress.TryParse(t, out _);
    }
}
