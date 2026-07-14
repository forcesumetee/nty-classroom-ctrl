using MessagePack;

namespace ClassroomCtrl.Shared.Protocol;

/// <summary>Lifecycle stage carried by <see cref="FileReceivedNotifyMessage.Status"/>.</summary>
public enum FileNotifyStatus : byte
{
    /// <summary>Announce arrived; chunks are streaming in.</summary>
    Receiving = 0,
    /// <summary>Reassembled, SHA-256 verified, and written to disk.</summary>
    Saved = 1,
    /// <summary>Transfer failed (truncated, hash mismatch, or write error) — nothing saved.</summary>
    Failed = 2,
}

/// <summary>
/// IPC-only (Service→Agent, <see cref="MessageType.FileReceivedNotify"/>) notification that a teacher
/// file transfer changed state on the daemon. The file I/O is guaranteed by the background service; this
/// exists purely so the tray Agent can surface a toast/progress when it happens to be running.
///
/// Lives in Shared.Wire so BOTH sides share the contract: the daemon (MacClassroomWorker) serializes it,
/// and the tray Agent (DaemonIpcClient) deserializes it.
/// </summary>
[MessagePackObject]
public class FileReceivedNotifyMessage
{
    [Key(0)] public Guid TransferId { get; set; }
    [Key(1)] public string FileName { get; set; } = "";
    /// <summary>One of <see cref="FileNotifyStatus"/> (byte for a forward-compatible wire).</summary>
    [Key(2)] public byte Status { get; set; }
    /// <summary>Absolute path the file was saved to (only when <see cref="Status"/> == Saved).</summary>
    [Key(3)] public string? SavedPath { get; set; }
    [Key(4)] public long SizeBytes { get; set; }
    /// <summary>Human-readable failure reason (only when <see cref="Status"/> == Failed).</summary>
    [Key(5)] public string? Error { get; set; }
}
