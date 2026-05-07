using ClassroomCtrl.Exam.Shared;
using DocumentFormat.OpenXml.Packaging;
using System.IO;
using System.Text.RegularExpressions;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 13b: Parses .docx files into Exam objects.
///
/// Expected format (English / Thai):
///   1. Question text here?
///   A) Choice 1
///   B) Choice 2
///   *C) Choice 3   ← asterisk prefix marks the correct choice
///   D) Choice 4
///
///   2. Next question...
///
/// Heuristics:
///   - A line starting with "N." (digits + dot) begins a new question
///   - A line starting with "X)" or "*X)" (X = letter or digit) is a choice
///   - Lines without those markers append to the current question text
///   - Asterisk prefix on a choice = correct answer
/// </summary>
public static class WordImportService
{
    private static readonly Regex QuestionStart = new(@"^\s*(\d+)\s*[\.\)]\s*(.*)$", RegexOptions.Compiled);
    private static readonly Regex ChoiceStart = new(@"^\s*(\*?)\s*([A-Za-zก-๙])\s*[\.\)]\s*(.*)$", RegexOptions.Compiled);

    public static ExamModel ImportFromDocx(string filePath, string defaultTitle)
    {
        var lines = ReadParagraphs(filePath);

        var exam = new ExamModel
        {
            Title = string.IsNullOrWhiteSpace(defaultTitle)
                ? Path.GetFileNameWithoutExtension(filePath)
                : defaultTitle,
        };

        Question? current = null;
        int order = 0;

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;

            var qMatch = QuestionStart.Match(line);
            if (qMatch.Success)
            {
                if (current != null) FinalizeQuestion(current, exam);
                current = new Question
                {
                    Order = order++,
                    Type = QuestionType.McqSingle,
                    Text = qMatch.Groups[2].Value.Trim(),
                    Points = 1,
                };
                continue;
            }

            if (current != null)
            {
                var cMatch = ChoiceStart.Match(line);
                if (cMatch.Success)
                {
                    var isCorrect = cMatch.Groups[1].Value == "*";
                    var label = cMatch.Groups[2].Value;
                    var text = cMatch.Groups[3].Value.Trim();
                    current.Choices.Add(new Choice { Label = label, Text = text });
                    if (isCorrect) current.CorrectChoiceLabels.Add(label);
                    continue;
                }

                // Continuation of question text
                current.Text = current.Text.Length == 0 ? line : current.Text + " " + line.Trim();
            }
        }

        if (current != null) FinalizeQuestion(current, exam);

        return exam;
    }

    private static void FinalizeQuestion(Question q, ExamModel exam)
    {
        // McqMulti if multiple correct, single otherwise
        if (q.CorrectChoiceLabels.Count > 1) q.Type = QuestionType.McqMulti;
        // Skip questions with no choices
        if (q.Choices.Count == 0) return;
        exam.Questions.Add(q);
    }

    private static IEnumerable<string> ReadParagraphs(string filePath)
    {
        using var doc = WordprocessingDocument.Open(filePath, isEditable: false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body == null) yield break;

        foreach (var p in body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>())
        {
            var text = string.Concat(p.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>()
                .Select(t => t.Text));
            yield return text;
        }
    }
}
