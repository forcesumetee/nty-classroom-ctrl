namespace ClassroomCtrl.Shared.Models;

public record StudentInfo(
    Guid EndpointId,
    string MachineName,
    string DisplayName,
    string IpAddress,
    bool IsConnected,
    bool IsHandRaised,
    Guid? CurrentRoomId,
    DateTime LastSeenUtc);
