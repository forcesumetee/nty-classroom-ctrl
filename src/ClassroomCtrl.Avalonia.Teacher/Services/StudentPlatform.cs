using System;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>The per-student actions the Teacher context menu can request (TT-5).
/// Lock/Unlock are enforced by BOTH Mac and Windows students; the three power
/// actions are executed by Windows students but are a no-op on Mac students
/// (no macOS power handler yet — a Student-track gap), so the tile gates them
/// via <see cref="StudentPlatform.CanReceivePower"/>.</summary>
public enum StudentCommand { Lock, Unlock, Logoff, Restart, Shutdown }

/// <summary>Carries the chosen <see cref="StudentCommand"/> from a tile's context menu
/// up to the window (which resolves the target student from the sender's DataContext),
/// mirroring TT-3's DoubleTapped→StudentActivated flow.</summary>
public sealed class StudentCommandEventArgs : EventArgs
{
    public StudentCommand Command { get; }
    public StudentCommandEventArgs(StudentCommand command) => Command = command;
}

/// <summary>
/// TT-5-B (macOS port) — the Teacher-side capability policy: which per-student
/// actions to OFFER for a given student, decided from the student's reported OS
/// (<c>HelloMessage.OsVersion</c>, already on the wire).
///
/// The only capability that varies today is POWER. A macOS Teacher can send
/// logoff/restart/shutdown to a Windows Student (it executes) but NOT to a Mac
/// Student (the Sandbox has no power handler — the command is silently ignored).
/// Per the TT-2 principle "a menu item that does nothing is worse than no menu
/// item," the tile DISABLES power on non-Windows students with a tooltip, rather
/// than shipping a dead action.
///
/// The rule is DEFAULT-DENY and keyed on the one unambiguous marker: power is
/// offered iff the OS string contains "Windows". This is reliable by construction —
/// the Windows contract (<c>Environment.OSVersion.VersionString</c>) ALWAYS yields
/// "Microsoft Windows NT ..."; the Mac string (<c>RuntimeInformation.OSDescription</c>,
/// measured "macOS 26.5.2") never does, nor does an empty/unknown string. A future
/// Linux student would correctly get power disabled until its execution is proven.
/// </summary>
public static class StudentPlatform
{
    /// <summary>True iff this student can EXECUTE power commands today (Windows only).
    /// Default-deny: empty/unknown/macOS/Linux → false.</summary>
    public static bool CanReceivePower(string? osVersion) =>
        !string.IsNullOrWhiteSpace(osVersion) &&
        osVersion.Contains("Windows", StringComparison.OrdinalIgnoreCase);
}
