using ClassroomCtrl.Exam.Shared;
using ClosedXML.Excel;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 13c: Exports exam results to .xlsx with one sheet "Results".
/// Columns: Display Name, Endpoint Id, Q1, Q2, ..., Total, %, Submitted At, Auto-Submitted
/// Last 3 rows: Average / Max / Min of Total + %.
/// </summary>
public static class ExcelExportService
{
    public static void ExportResults(ExamModel exam, IEnumerable<Submission> submissions, string outputPath)
    {
        using var workbook = new XLWorkbook();
        var ws = workbook.Worksheets.Add("Results");

        int row = 1;
        int col = 1;

        // Phase 13f: Student-info columns first — schools paste these into report cards.
        // Order matches the LockdownExamWindow info form: FullName, ClassName, StudentNumber.
        ws.Cell(row, col++).Value = "ชื่อ-นามสกุล";        // FullName
        ws.Cell(row, col++).Value = "ชั้น";                   // ClassName
        ws.Cell(row, col++).Value = "เลขที่";                 // StudentNumber
        ws.Cell(row, col++).Value = "Endpoint";               // diagnostic id

        for (int i = 0; i < exam.Questions.Count; i++)
        {
            ws.Cell(row, col++).Value = $"Q{i + 1}";
        }
        ws.Cell(row, col++).Value = "Total";
        ws.Cell(row, col++).Value = "%";
        ws.Cell(row, col++).Value = "Submitted";
        ws.Cell(row, col++).Value = "Auto-Submitted";

        var headerRange = ws.Range(1, 1, 1, col - 1);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;

        // Body
        var subs = submissions.ToList();
        foreach (var sub in subs)
        {
            row++;
            col = 1;
            // Fall back to DisplayName when student didn't fill the info form (shouldn't happen in normal flow).
            var info = sub.Student ?? new StudentInfo();
            ws.Cell(row, col++).Value = string.IsNullOrEmpty(info.FullName) ? sub.DisplayName : info.FullName;
            ws.Cell(row, col++).Value = info.ClassName;
            ws.Cell(row, col++).Value = info.StudentNumber;
            ws.Cell(row, col++).Value = sub.StudentEndpointId.ToString("N");

            var byQ = sub.Answers.ToDictionary(a => a.QuestionId);
            foreach (var q in exam.Questions)
            {
                if (byQ.TryGetValue(q.Id, out var a))
                {
                    var disp = q.Type switch
                    {
                        QuestionType.Essay or QuestionType.ShortAnswer => a.EssayText,
                        _ => string.Join(",", a.SelectedLabels),
                    };
                    ws.Cell(row, col++).Value = disp;
                }
                else
                {
                    ws.Cell(row, col++).Value = "";
                }
            }
            ws.Cell(row, col++).Value = sub.TotalScore;
            ws.Cell(row, col++).Value = GradingService.Percentage(sub);
            ws.Cell(row, col++).Value = sub.SubmittedAtUtc.ToLocalTime();
            ws.Cell(row, col++).Value = sub.IsAutoSubmitted ? "Yes" : "";
        }

        // Footer stats — column offsets shifted by +2 vs. pre-13f because
        // 3 student-info columns + 1 endpoint = 4 columns precede Q1 (was 2).
        if (subs.Count > 0)
        {
            row += 2;
            int totalCol = 5 + exam.Questions.Count; // Total column (was 3 + N)
            int pctCol = totalCol + 1;

            ws.Cell(row, 1).Value = "Average";
            ws.Cell(row, totalCol).Value = subs.Average(s => s.TotalScore);
            ws.Cell(row, pctCol).Value = subs.Average(s => GradingService.Percentage(s));
            row++;
            ws.Cell(row, 1).Value = "Max";
            ws.Cell(row, totalCol).Value = subs.Max(s => s.TotalScore);
            ws.Cell(row, pctCol).Value = subs.Max(s => GradingService.Percentage(s));
            row++;
            ws.Cell(row, 1).Value = "Min";
            ws.Cell(row, totalCol).Value = subs.Min(s => s.TotalScore);
            ws.Cell(row, pctCol).Value = subs.Min(s => GradingService.Percentage(s));
        }

        ws.Columns().AdjustToContents();
        workbook.SaveAs(outputPath);
    }
}
