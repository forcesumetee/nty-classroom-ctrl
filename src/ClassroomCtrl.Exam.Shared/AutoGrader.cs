namespace ClassroomCtrl.Exam.Shared;

public static class AutoGrader
{
    /// <summary>
    /// MCQ + ShortAnswer auto-grading per Spec §13.6.
    /// MC single: full points if exact match, else 0.
    /// MC multi: full points if exact set match (no extra/missing), else partial-credit if enabled.
    /// True/False: graded as McqSingle.
    /// ShortAnswer: case-insensitive trim compare against Question.CorrectShortAnswer.
    /// Essay: returns 0 (manual grading required).
    /// </summary>
    public static int Score(Question q, Answer a, bool partialCreditForMulti = false)
    {
        if (q.Type == QuestionType.Essay) return 0;

        if (q.Type == QuestionType.ShortAnswer)
        {
            var expected = (q.CorrectShortAnswer ?? "").Trim();
            var got = (a.EssayText ?? "").Trim();
            if (expected.Length == 0) return 0;
            return expected.Equals(got, StringComparison.OrdinalIgnoreCase) ? q.Points : 0;
        }

        var correct = new HashSet<string>(q.CorrectChoiceLabels, StringComparer.OrdinalIgnoreCase);
        var picked = new HashSet<string>(a.SelectedLabels, StringComparer.OrdinalIgnoreCase);

        if (q.Type == QuestionType.McqSingle || q.Type == QuestionType.TrueFalse)
            return picked.Count == 1 && correct.SetEquals(picked) ? q.Points : 0;

        if (correct.SetEquals(picked)) return q.Points;
        if (!partialCreditForMulti) return 0;

        // Partial credit: per-correct-choice point split, penalize wrong picks
        int correctPicks = picked.Count(p => correct.Contains(p));
        int wrongPicks = picked.Count - correctPicks;
        double perChoice = (double)q.Points / correct.Count;
        double score = (correctPicks - wrongPicks) * perChoice;
        return Math.Max(0, (int)Math.Round(score));
    }

    public static int ScoreSubmission(Exam exam, Submission s, bool partialCreditForMulti = false)
    {
        int total = 0;
        var byId = exam.Questions.ToDictionary(q => q.Id);
        foreach (var ans in s.Answers)
            if (byId.TryGetValue(ans.QuestionId, out var q))
                total += Score(q, ans, partialCreditForMulti);
        return total;
    }
}
