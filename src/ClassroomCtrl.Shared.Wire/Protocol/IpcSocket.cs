namespace ClassroomCtrl.Shared.Protocol;

/// <summary>
/// Well-known IPC endpoints shared by the macOS daemon (Service) and the tray Agent, so the two
/// processes can never drift on the path. Framing on this socket is the same length-prefixed
/// MessagePack(Envelope) used everywhere else: <c>[4-byte big-endian Int32 length] + payload</c>.
/// </summary>
public static class IpcSocket
{
    /// <summary>
    /// Unix domain socket for Service ↔ Agent IPC: <c>~/Library/Caches/NTY/classroom-ipc.sock</c>.
    /// Under the user's Caches dir (not <c>/tmp</c>) so it's per-user and not world-writable.
    /// </summary>
    public static string StudentAgentPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Caches", "NTY", "classroom-ipc.sock");
}
