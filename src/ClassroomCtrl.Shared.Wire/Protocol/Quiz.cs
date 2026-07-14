using MessagePack;

namespace ClassroomCtrl.Shared.Protocol;

/// <summary>
/// A single-question survey/quiz the Teacher pushes to students. Carried on the reserved
/// <see cref="MessageType.QuizStart"/> codepoint (Teacher→Daemon) and re-broadcast to the Agent as
/// <see cref="MessageType.QuizBroadcast"/> over IPC.
///
/// NOTE: this is the simple survey model, deliberately NOT the shipped Windows multi-question
/// <c>Exam.Shared</c> payload (which is a deferred port). Field naming mirrors the shipped concepts
/// (QuizId ~ SessionId) so the two can be reconciled when the full exam system ports.
/// </summary>
[MessagePackObject]
public class QuizQuestionMessage
{
    /// <summary>Correlates a submitted answer back to this question.</summary>
    [Key(0)] public Guid QuizId { get; set; }
    [Key(1)] public string Question { get; set; } = "";
    [Key(2)] public List<string> Options { get; set; } = new();
    /// <summary>Reserved for future multi-select; single-choice (radio) today.</summary>
    [Key(3)] public bool AllowMultiple { get; set; }
}

/// <summary>
/// IPC-only (Agent→Service, <see cref="MessageType.QuizSubmitRequest"/>) — the student's chosen option.
/// The Agent sends the selection; the daemon owns identity and wraps it into a <see cref="QuizAnswerMessage"/>.
/// </summary>
[MessagePackObject]
public class QuizSubmitRequestMessage
{
    [Key(0)] public Guid QuizId { get; set; }
    [Key(1)] public int SelectedIndex { get; set; }
    [Key(2)] public string SelectedText { get; set; } = "";
}

/// <summary>
/// The student's answer sent to the Teacher on the reserved <see cref="MessageType.QuizAnswerSubmit"/>
/// codepoint. Built by the daemon (which stamps the real endpoint id + display name) from the Agent's
/// <see cref="QuizSubmitRequestMessage"/>.
/// </summary>
[MessagePackObject]
public class QuizAnswerMessage
{
    [Key(0)] public Guid QuizId { get; set; }
    [Key(1)] public Guid StudentEndpointId { get; set; }
    [Key(2)] public string StudentName { get; set; } = "";
    [Key(3)] public int SelectedIndex { get; set; }
    [Key(4)] public string SelectedText { get; set; } = "";
    [Key(5)] public long TimestampUtcMs { get; set; }
}
