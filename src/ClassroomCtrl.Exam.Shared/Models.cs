using MessagePack;

namespace ClassroomCtrl.Exam.Shared;

public enum QuestionType : byte
{
    McqSingle = 1,
    McqMulti = 2,
    Essay = 3,
    /// <summary>Phase 13: Short-answer text matched case-insensitively against Question.CorrectShortAnswer.</summary>
    ShortAnswer = 4,
    /// <summary>Phase 13: True/False — stored as McqSingle with 2 fixed choices, but flagged for UI rendering.</summary>
    TrueFalse = 5,
}

// All Exam.Shared classes use [MessagePackObject(keyAsPropertyName: true)] (a.k.a. "true").
// Why not [Key(N)]? With strict-keyed mode, every public property must be either keyed
// or [IgnoreMember], and Exam.TotalPoints (a computed get-only property) caused the
// "Failed to serialize ClassroomCtrl.Exam.Shared.QuizStartPayload" runtime error in
// Phase 13 send-quiz testing. keyAsPropertyName mode auto-skips get-only properties
// without setters, so no extra ceremony is needed when adding computed members later.

[MessagePackObject(true)]
public class Choice
{
    public string Label { get; set; } = ""; // "ก", "ข", "A", "B", ...
    public string Text { get; set; } = "";
    public string? ImagePath { get; set; }  // path inside .ntyexam zip
}

[MessagePackObject(true)]
public class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int Order { get; set; }
    public QuestionType Type { get; set; }
    public string Text { get; set; } = "";
    public string? ImagePath { get; set; }
    public List<Choice> Choices { get; set; } = new();
    public List<string> CorrectChoiceLabels { get; set; } = new(); // for MCQ
    public int Points { get; set; } = 1;
    public int EssayMaxChars { get; set; } = 2000;
    public string? Rubric { get; set; }
    /// <summary>Phase 13: Expected text for ShortAnswer questions. Compared case-insensitively after Trim.</summary>
    public string CorrectShortAnswer { get; set; } = "";
}

[MessagePackObject(true)]
public class Exam
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Subject { get; set; } = "";
    public List<Question> Questions { get; set; } = new();
    public int TimeLimitMinutes { get; set; } = 60;
    public bool ShuffleQuestions { get; set; }
    public bool ShuffleChoices { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    /// <summary>Phase 13: Free-form description shown to student before starting.</summary>
    public string Description { get; set; } = "";
    /// <summary>Phase 13: If true, Student sees their score after submission. Otherwise shows "submitted".</summary>
    public bool ShowScoreToStudent { get; set; } = true;

    // Computed (get-only without setter) — keyAsPropertyName mode skips this automatically.
    [IgnoreMember]
    public int TotalPoints => Questions.Sum(q => q.Points);
}

/// <summary>
/// Phase 13f: Student-supplied identity captured on the LockdownExamWindow pre-test page.
/// Replaces the original FirstName/LastName/Grade scheme — schools want a single
/// "ชื่อ-นามสกุล" field to avoid name-order bugs across cultures.
/// </summary>
[MessagePackObject(true)]
public class StudentInfo
{
    public string FullName { get; set; } = "";
    public string ClassName { get; set; } = "";   // ชั้น (เช่น ม.4/2, ป.6/1)
    public string StudentNumber { get; set; } = ""; // เลขที่
}

[MessagePackObject(true)]
public class Answer
{
    public Guid QuestionId { get; set; }
    public List<string> SelectedLabels { get; set; } = new(); // for MCQ
    public string EssayText { get; set; } = "";
}

[MessagePackObject(true)]
public class Submission
{
    public Guid ExamId { get; set; }
    public Guid StudentEndpointId { get; set; }
    public StudentInfo Student { get; set; } = new();
    public DateTime StartedAtUtc { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public List<Answer> Answers { get; set; } = new();
    public int? AutoMcqScore { get; set; }
    public int? ManualEssayScore { get; set; }
    /// <summary>Phase 13: True if submitted by timer expiry rather than student click.</summary>
    public bool IsAutoSubmitted { get; set; }
    /// <summary>Phase 13: Display name from Hello (used in results UI when no StudentInfo provided).</summary>
    public string DisplayName { get; set; } = "";
    /// <summary>Phase 13: Cached total score (Auto + Manual); GradingService writes this.</summary>
    public int TotalScore { get; set; }
    /// <summary>Phase 13: Cached MaxScore (sum of Question.Points).</summary>
    public int MaxScore { get; set; }
}

// ───────────── Phase 13: Quiz protocol payloads ─────────────

/// <summary>Phase 13: Sent by teacher when starting a quiz session — contains the full Exam.</summary>
[MessagePackObject(true)]
public class QuizStartPayload
{
    public Guid SessionId { get; set; }
    public Exam Exam { get; set; } = new();
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Phase 13: Student → Teacher submission of answers.</summary>
[MessagePackObject(true)]
public class QuizAnswerSubmitPayload
{
    public Guid SessionId { get; set; }
    public Guid ExamId { get; set; }
    public Guid StudentEndpointId { get; set; }
    public string DisplayName { get; set; } = "";
    /// <summary>Phase 13f: Student identity captured on pre-test info page.</summary>
    public StudentInfo Student { get; set; } = new();
    public List<Answer> Answers { get; set; } = new();
    public bool IsAutoSubmitted { get; set; }
    public DateTime SubmittedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Phase 13: Teacher → Students "exam ended" — closes lockdown window.</summary>
[MessagePackObject(true)]
public class QuizEndPayload
{
    public Guid SessionId { get; set; }
    public bool ShowResultsToStudents { get; set; } = true;
}
