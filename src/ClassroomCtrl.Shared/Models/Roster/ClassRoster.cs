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

[MessagePackObject(true)]
public class EnrolledStudent
{
    public string FullName { get; set; } = "";
    public string ClassNameTh { get; set; } = "";  // e.g. "ม.4/2"
    public string StudentNumber { get; set; } = ""; // e.g. "12"
    public string MachineName { get; set; } = "";
    public DateTime EnrolledAt { get; set; } = DateTime.UtcNow;

    /// <summary>Computed at runtime by ClassRosterService.MarkAttendance.</summary>
    [IgnoreMember]
    public bool IsPresent { get; set; }
}
