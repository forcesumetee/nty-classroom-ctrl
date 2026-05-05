namespace ClassroomCtrl.Shared.Protocol;

public static class NetworkConstants
{
    public const int ControlTcpPort = 7777;
    public const int DiscoveryUdpPort = 7778;
    public const int MediaRtpPortBase = 7779;
    public const int FileMulticastPort = 7780;
    public const string FileMulticastGroup = "239.10.10.10";
    public const string MdnsServiceType = "_classroomctl._tcp.local.";
    public const string IpcPipeName = "ClassroomCtrl";
    public const int ProtocolVersion = 1;
}
