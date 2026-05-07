using ClassroomCtrl.Exam.Shared;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Teacher.Services;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;
using MessageBox = System.Windows.MessageBox;

namespace ClassroomCtrl.Teacher.Quiz;

public partial class ExamResultsWindow : Window
{
    private readonly ExamModel _exam;
    private readonly ObservableCollection<RowVM> _rows = new();

    public ExamResultsWindow(ExamModel exam)
    {
        InitializeComponent();
        _exam = exam;
        QuizTitleText.Text = exam.Title;
        ResultsGrid.ItemsSource = _rows;

        if (App.Exam != null)
        {
            App.Exam.SubmissionsChanged += OnSubmissionsChanged;
            Refresh();
        }
        Closed += (_, _) =>
        {
            if (App.Exam != null) App.Exam.SubmissionsChanged -= OnSubmissionsChanged;
        };
    }

    private void OnSubmissionsChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(Refresh);
    }

    private void Refresh()
    {
        if (App.Exam == null) return;
        _rows.Clear();
        foreach (var s in App.Exam.CurrentSubmissions)
        {
            var info = s.Student ?? new ClassroomCtrl.Exam.Shared.StudentInfo();
            _rows.Add(new RowVM
            {
                FullName = !string.IsNullOrEmpty(info.FullName) ? info.FullName
                    : string.IsNullOrEmpty(s.DisplayName) ? s.StudentEndpointId.ToString("N").Substring(0, 8) : s.DisplayName,
                ClassName = info.ClassName,
                StudentNumber = info.StudentNumber,
                ScoreText = $"{s.TotalScore}/{s.MaxScore}",
                Percentage = GradingService.Percentage(s),
                SubmittedText = s.SubmittedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                AutoSubmittedText = s.IsAutoSubmitted ? Loc.Get("Lbl_AutoSubmitted") : "",
                Submission = s,
            });
        }
        if (_rows.Count == 0)
        {
            StatsText.Text = Loc.Get("Lbl_NoSubmissions");
        }
        else
        {
            var avg = _rows.Average(r => r.Percentage);
            var max = _rows.Max(r => r.Percentage);
            var min = _rows.Min(r => r.Percentage);
            StatsText.Text = Loc.Format("Lbl_StatsLine", _rows.Count, avg, max, min);
        }

        // Phase 1.2: refresh statistics view
        StatsView?.LoadStats(_exam, _rows.Select(r => r.Submission));
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = Loc.Get("Btn_ExportExcel"),
            Filter = "Excel files (*.xlsx)|*.xlsx",
            FileName = $"exam-{SanitizeFile(_exam.Title)}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var subs = _rows.Select(r => r.Submission).ToList();
            ExcelExportService.ExportResults(_exam, subs, dlg.FileName);
            MessageBox.Show(Loc.Format("Msg_ExportSaved", dlg.FileName));
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}");
        }
    }

    private void ExportHtml_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = Loc.Get("Btn_ExportHtml"),
            Filter = "HTML files (*.html)|*.html",
            FileName = $"exam-{SanitizeFile(_exam.Title)}-{DateTime.Now:yyyyMMdd-HHmm}.html",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var subs = _rows.Select(r => r.Submission).ToList();
            HtmlExportService.ExportResults(_exam, subs, dlg.FileName);
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = dlg.FileName, UseShellExecute = true }); } catch { }
            MessageBox.Show(Loc.Format("Msg_ExportSaved", dlg.FileName));
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string SanitizeFile(string s)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        return new string(s.Where(c => !bad.Contains(c)).ToArray());
    }

    private class RowVM
    {
        public string FullName { get; set; } = "";
        public string ClassName { get; set; } = "";
        public string StudentNumber { get; set; } = "";
        public string ScoreText { get; set; } = "";
        public int Percentage { get; set; }
        public string SubmittedText { get; set; } = "";
        public string AutoSubmittedText { get; set; } = "";
        public Submission Submission { get; set; } = new();
    }
}
