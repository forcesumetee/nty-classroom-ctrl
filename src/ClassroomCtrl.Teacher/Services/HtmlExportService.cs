using ClassroomCtrl.Exam.Shared;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 14: Export exam results to a self-contained HTML file. Schools without
/// Excel can open the report in any browser.
/// </summary>
public static class HtmlExportService
{
    public static void ExportResults(ExamModel exam, IEnumerable<Submission> submissions, string outputPath)
    {
        var subs = submissions.ToList();
        int totalPts = exam.TotalPoints;

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"th\"><head><meta charset=\"utf-8\"/>");
        sb.Append("<title>").Append(Esc(exam.Title)).Append("</title>");
        sb.Append("<style>");
        sb.Append("body{font-family:Segoe UI,Tahoma,sans-serif;background:#f5f5f5;margin:24px;color:#1f2937;}");
        sb.Append("h1{color:#1e40af;margin-bottom:4px;}");
        sb.Append(".meta{color:#475569;margin-bottom:16px;}");
        sb.Append("table{border-collapse:collapse;background:white;width:100%;box-shadow:0 1px 3px rgba(0,0,0,0.1);}");
        sb.Append("th,td{border:1px solid #cbd5e1;padding:6px 10px;text-align:left;font-size:13px;}");
        sb.Append("th{background:#1e40af;color:white;}");
        sb.Append("tr:nth-child(even){background:#f8fafc;}");
        sb.Append(".pass{color:#10b981;font-weight:bold;}");
        sb.Append(".fail{color:#dc2626;font-weight:bold;}");
        sb.Append(".center{text-align:center;}");
        sb.Append(".sum{background:#fef3c7;font-weight:bold;}");
        sb.Append("</style></head><body>");

        sb.Append("<h1>").Append(Esc(exam.Title)).Append("</h1>");
        sb.Append("<div class=\"meta\">");
        sb.Append("Questions: ").Append(exam.Questions.Count);
        sb.Append(" · Total points: ").Append(totalPts);
        sb.Append(" · Submissions: ").Append(subs.Count);
        sb.Append("</div>");

        sb.Append("<table><thead><tr>");
        sb.Append("<th>ชื่อ-นามสกุล</th><th>ชั้น</th><th>เลขที่</th>");
        for (int i = 0; i < exam.Questions.Count; i++)
            sb.Append("<th class=\"center\">Q").Append(i + 1).Append("</th>");
        sb.Append("<th class=\"center\">Total</th><th class=\"center\">%</th><th>Submitted</th>");
        sb.Append("</tr></thead><tbody>");

        foreach (var sub in subs)
        {
            var info = sub.Student ?? new StudentInfo();
            var fullName = string.IsNullOrEmpty(info.FullName) ? sub.DisplayName : info.FullName;

            sb.Append("<tr>");
            sb.Append("<td>").Append(Esc(fullName)).Append("</td>");
            sb.Append("<td>").Append(Esc(info.ClassName)).Append("</td>");
            sb.Append("<td>").Append(Esc(info.StudentNumber)).Append("</td>");

            int score = 0;
            for (int i = 0; i < exam.Questions.Count; i++)
            {
                var q = exam.Questions[i];
                var ans = sub.Answers.FirstOrDefault(a => a.QuestionId == q.Id);
                bool correct = ans != null && IsCorrect(q, ans);
                if (correct) score += q.Points;
                sb.Append("<td class=\"center ").Append(correct ? "pass\">✓" : "fail\">✗").Append("</td>");
            }
            int pct = totalPts > 0 ? (int)System.Math.Round(score * 100.0 / totalPts) : 0;
            sb.Append("<td class=\"center\">").Append(score).Append('/').Append(totalPts).Append("</td>");
            sb.Append("<td class=\"center\">").Append(pct).Append("%</td>");
            sb.Append("<td>").Append(sub.SubmittedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")).Append("</td>");
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table>");

        if (subs.Count > 0)
        {
            var scores = subs.Select(s => ScoreOf(exam, s)).ToList();
            int avg = (int)System.Math.Round(scores.Average());
            int max = scores.Max();
            int min = scores.Min();
            sb.Append("<table style=\"margin-top:16px;width:auto;\"><tr class=\"sum\"><th>Average</th><td>").Append(avg).Append("</td>");
            sb.Append("<th>Max</th><td>").Append(max).Append("</td>");
            sb.Append("<th>Min</th><td>").Append(min).Append("</td></tr></table>");
        }

        sb.Append("</body></html>");
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    private static int ScoreOf(ExamModel exam, Submission sub)
    {
        int s = 0;
        foreach (var q in exam.Questions)
        {
            var a = sub.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
            if (a != null && IsCorrect(q, a)) s += q.Points;
        }
        return s;
    }

    private static bool IsCorrect(Question q, Answer a)
    {
        if (q.CorrectChoiceLabels == null || q.CorrectChoiceLabels.Count == 0) return false;
        var correct = q.CorrectChoiceLabels.ToHashSet();
        var picked = a.SelectedLabels?.ToHashSet() ?? new System.Collections.Generic.HashSet<string>();
        return picked.SetEquals(correct);
    }

    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }
}
