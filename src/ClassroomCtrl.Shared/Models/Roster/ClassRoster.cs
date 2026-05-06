using MessagePack;
using System;
using System.Collections.Generic;

namespace ClassroomCtrl.Shared.Models.Roster;

/// <summary>
/// Phase 9.3: A saved classroom — name + enrolled students. Persisted as JSON
/// at %ProgramData%\NTY\ClassroomCtrl\Rosters\{Id}.json. MessagePack attribute is
/// kept so roster can also travel across the wire if a future "share roster"
/// feature wants to push it from teacher to student.
/// </summary>
[MessagePackObject(true)]
public class ClassRoster
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ClassName { get; set; } = "";
    public string Description { get; set; } = "";
    public List<EnrolledStudent> Students { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Phase 7.1 — full attendance status set used by the Mark Attendance dialog.</summary>
public enum AttendanceStatus { Present, Absent, Late, Excused }

[MessagePackObject(true)]
public class EnrolledStudent
{
    public string FullName { get; set; } = "";
    public string ClassNameTh { get; set; } = "";  // e.g. "ม.4/2"
    public string StudentNumber { get; set; } = ""; // e.g. "12"
    public string MachineName { get; set; } = "";
    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;

    /// <summary>Legacy boolean kept for ClassRosterService.MarkAttendance() and other
    /// callers that just want a yes/no.  Phase 7.1 mirrors AttendanceStatus into it
    /// on Save so the existing summaries (Chat_AttendanceMarked) keep working.</summary>
    [IgnoreMember]
    public bool IsPresent { get; set; }

    /// <summary>Phase 7.1 — full Mark Attendance state.  Persisted to disk via
    /// System.Text.Json (no [JsonIgnore]) but excluded from MessagePack/wire so it
    /// stays a teacher-side bookkeeping concern, not synced to students.  Default
    /// is Absent — for any roster file that pre-dates this field, HasBeenMarked
    /// will deserialize as false so the dialog's first-open logic still applies
    /// the connected→Present default.</summary>
    [IgnoreMember]
    public AttendanceStatus AttendanceStatus { get; set; } = AttendanceStatus.Absent;

    /// <summary>Phase 7.1 — true once the teacher has clicked Save in the Mark
    /// Attendance dialog at least once for this student.  Lets the dialog
    /// distinguish "first-open this roster, use connected-status default" from
    /// "re-open, restore last saved selection".</summary>
    [IgnoreMember]
    public bool HasBeenMarked { get; set; }
}
