namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>TT-7-C — the notification sounds the Teacher plays.</summary>
public enum NotificationSound
{
    /// <summary>A student raised their hand (attention-grabbing).</summary>
    HandRaise,
    /// <summary>An incoming chat message (soft).</summary>
    Chat,
}

/// <summary>
/// TT-7-C — plays short notification sounds. An interface so the TT-7-E aggregation gate
/// can inject a recording fake and assert the DISTINGUISHING property — that a hand-raise
/// actually triggers a HandRaise sound (not just that a tile flag flipped).
/// </summary>
public interface ISoundService
{
    void Play(NotificationSound sound);
}
