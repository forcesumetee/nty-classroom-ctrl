using ClassroomCtrl.Exam.Shared;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 13e: Wraps Exam.Shared.AutoGrader, fills in MaxScore + TotalScore + DisplayName
/// for the Submission DTO so the results UI can display them without re-grading.
/// </summary>
public static class GradingService
{
    public static Submission Grade(ExamModel exam, QuizAnswerSubmitPayload submission)
    {
        var s = new Submission
        {
            ExamId = exam.Id,
            StudentEndpointId = submission.StudentEndpointId,
            DisplayName = submission.DisplayName,
            Student = submission.Student,  // Phase 13f: pre-test student info
            Answers = submission.Answers,
            StartedAtUtc = submission.SubmittedAtUtc, // we don't track Started on Teacher side
            SubmittedAtUtc = submission.SubmittedAtUtc,
            IsAutoSubmitted = submission.IsAutoSubmitted,
            MaxScore = exam.TotalPoints,
        };

        s.AutoMcqScore = AutoGrader.ScoreSubmission(exam, s, partialCreditForMulti: true);
        s.TotalScore = s.AutoMcqScore ?? 0;
        return s;
    }

    public static int Percentage(Submission s)
        => s.MaxScore > 0 ? (int)Math.Round(100.0 * s.TotalScore / s.MaxScore) : 0;
}
