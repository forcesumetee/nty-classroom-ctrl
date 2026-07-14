using System;

namespace ClassroomCtrl.Teacher.Core;

/// <summary>The per-student actions the Teacher can request (TT-5). Lock/Unlock are
/// enforced by BOTH Mac and Windows students; the three power actions are executed by
/// Windows students but are a no-op on Mac students (no macOS power handler yet — a
/// Student-track gap), so callers gate them via <see cref="StudentPlatform.CanReceivePower"/>.
///
/// UI-agnostic domain vocabulary — lives in Teacher.Core (with StudentRoster) so the
/// controller and its committed gate (--teacherselftest) can use it without an Avalonia
/// dependency. The UI event wrapper (StudentCommandEventArgs) stays in the Teacher app.</summary>
public enum StudentCommand { Lock, Unlock, Logoff, Restart, Shutdown }

/// <summary>
/// TT-5-B (macOS port) — the Teacher-side capability policy: which per-student actions to
/// OFFER for a given student, decided from the student's reported OS
/// (<c>HelloMessage.OsVersion</c>, already on the wire). Promoted from the Teacher app into
/// Teacher.Core (the UI-agnostic, headless-testable layer) so the send-path guard is permanent.
///
/// The only capability that varies today is POWER. A macOS Teacher can send
/// logoff/restart/shutdown to a Windows Student (it executes) but NOT to a Mac Student (the
/// Sandbox has no power handler — the command is silently ignored). Per the TT-2 principle
/// "a menu item that does nothing is worse than no menu item," the tile DISABLES power on
/// non-Windows students with a tooltip, rather than shipping a dead action.
///
/// The rule is DEFAULT-DENY and keyed on the one unambiguous marker: power is offered iff the
/// OS string contains "Windows". This is reliable by construction — the Windows contract
/// (<c>Environment.OSVersion.VersionString</c>) ALWAYS yields "Microsoft Windows NT ..."; the
/// Mac string (<c>RuntimeInformation.OSDescription</c>, measured "macOS 26.5.2") never does,
/// nor does an empty/unknown string. A future Linux student would correctly get power disabled
/// until its execution is proven.
/// </summary>
public static class StudentPlatform
{
    /// <summary>True iff this student can EXECUTE power commands today (Windows only).
    /// Default-deny: empty/unknown/macOS/Linux → false.</summary>
    public static bool CanReceivePower(string? osVersion) =>
        !string.IsNullOrWhiteSpace(osVersion) &&
        osVersion.Contains("Windows", StringComparison.OrdinalIgnoreCase);
}
