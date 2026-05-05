using System;

namespace ClassroomCtrl.Shared.Telemetry;

/// <summary>
/// Phase 1: Local-only telemetry snapshot. Persisted to JSON every 60s. No
/// network egress until a backend endpoint exists.
/// </summary>
public class TelemetryPayload
{
    public string InstallId { get; set; } = "";
    public string MachineHash { get; set; } = "";
    public string AppVersion { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public TimeSpan Uptime { get; set; }
    public int SessionsCount { get; set; }
    public int ErrorCount { get; set; }
    public string LastErrorMessage { get; set; } = "";
    public DateTime LastSeenUtc { get; set; }
}
