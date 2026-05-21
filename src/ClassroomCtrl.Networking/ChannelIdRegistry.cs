using System;
using Microsoft.Win32;

namespace ClassroomCtrl.Networking;

/// <summary>
/// Phase 10.10 Fix 3 — single source of truth for the discovery ChannelId.
///
/// Before: Teacher wrote to <c>HKCU\Software\NTY\ClassroomCtrl</c>, Student wrote
/// to <c>HKCU\Software\NTY\ClassroomCtrl\Student</c>, and the Service ran as
/// LocalSystem so its <c>Registry.CurrentUser</c> pointed at the SYSTEM profile —
/// a different hive entirely from the user that had set the channel via the
/// Teacher UI.  Result: Teacher and Student.Service rarely agreed on the channel
/// in production.
///
/// After: both sides go through this class.  Reads try HKLM first, then HKCU as
/// a one-time backward-compat fallback (so a deployment that's been running on
/// the old paths keeps working until it's rewritten by the next admin save).
/// Writes target HKLM, which requires admin elevation; if that fails we log via
/// the supplied logger callback (or the caller's preference) and fall back to
/// HKCU so the value at least round-trips for the current user.
/// </summary>
public static class ChannelIdRegistry
{
    private const string HklmSubKey = @"Software\NTY\ClassroomCtrl";
    private const string HkcuTeacherSubKey = @"Software\NTY\ClassroomCtrl";
    private const string HkcuStudentSubKey = @"Software\NTY\ClassroomCtrl\Student";
    private const string ValueName = "ChannelId";
    /// <summary>Default channel — same value the legacy code used.</summary>
    public const string DefaultChannelId = "1234";

    /// <summary>
    /// HKLM first (machine-wide; matches both interactive user and LocalSystem),
    /// then HKCU\…\ClassroomCtrl (legacy Teacher path), then HKCU\…\Student
    /// (legacy Student path).  Returns <see cref="DefaultChannelId"/> if every
    /// lookup fails.  Never throws.
    /// </summary>
    public static string Read()
    {
        // Primary: HKLM (visible to both interactive user and LocalSystem service).
        try
        {
            using var hklm = Registry.LocalMachine.OpenSubKey(HklmSubKey);
            if (hklm?.GetValue(ValueName) is string hklmValue && !string.IsNullOrWhiteSpace(hklmValue))
                return hklmValue;
        }
        catch { /* fall through */ }

        // Backward compat: HKCU teacher legacy path.
        try
        {
            using var hkcuTeacher = Registry.CurrentUser.OpenSubKey(HkcuTeacherSubKey);
            if (hkcuTeacher?.GetValue(ValueName) is string teacherValue && !string.IsNullOrWhiteSpace(teacherValue))
                return teacherValue;
        }
        catch { /* fall through */ }

        // Backward compat: HKCU student legacy path.
        try
        {
            using var hkcuStudent = Registry.CurrentUser.OpenSubKey(HkcuStudentSubKey);
            if (hkcuStudent?.GetValue(ValueName) is string studentValue && !string.IsNullOrWhiteSpace(studentValue))
                return studentValue;
        }
        catch { /* fall through */ }

        return DefaultChannelId;
    }

    /// <summary>
    /// Writes to HKLM.  Requires admin; on failure falls back to HKCU so the
    /// caller's session still gets the new value.  Returns true if the HKLM
    /// write succeeded (= machine-wide effect including LocalSystem service);
    /// false if only the HKCU fallback was written (= effect limited to the
    /// current user — Teacher UI must surface this as a warning).
    /// </summary>
    public static bool Write(string id, Action<string>? warningLog = null)
    {
        var value = string.IsNullOrWhiteSpace(id) ? DefaultChannelId : id.Trim();

        try
        {
            using var hklm = Registry.LocalMachine.CreateSubKey(HklmSubKey, writable: true);
            hklm?.SetValue(ValueName, value, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            warningLog?.Invoke(
                $"ChannelIdRegistry: HKLM write failed ({ex.GetType().Name}: {ex.Message}); "
                + "falling back to HKCU. The Service running as LocalSystem will NOT see this "
                + "channel until an admin re-runs the Teacher UI elevated.");
        }

        try
        {
            using var hkcu = Registry.CurrentUser.CreateSubKey(HkcuTeacherSubKey);
            hkcu?.SetValue(ValueName, value, RegistryValueKind.String);
        }
        catch { /* swallow — best effort */ }
        return false;
    }
}
