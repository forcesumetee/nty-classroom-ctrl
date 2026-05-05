using MessagePack;

namespace ClassroomCtrl.Shared.Discovery;

/// <summary>
/// Phase 9.4: UDP discovery beacon emitted by the teacher every 2 seconds on
/// 255.255.255.255:7778. Students listen, filter by ChannelId, and auto-connect
/// to TeacherIp:TcpPort. ChannelId acts as a 4-digit "room number" so multiple
/// classrooms in the same building don't cross-connect.
/// </summary>
[MessagePackObject(true)]
public class BeaconPayload
{
    public string ChannelId { get; set; } = "1234";
    public string TeacherIp { get; set; } = "";
    public string ClassName { get; set; } = "";
    public int TcpPort { get; set; } = 7777;
    public long TimestampUtcMs { get; set; }
}
