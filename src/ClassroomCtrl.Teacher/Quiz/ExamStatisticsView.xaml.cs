using ClassroomCtrl.Exam.Shared;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;

namespace ClassroomCtrl.Teacher.Quiz;

public partial class ExamStatisticsView : UserControl
{
    public ExamStatisticsView()
    {
        InitializeComponent();
        // Chart-level transparency so the dark Surface.Elevated panel shows through
        // (LiveCharts defaults to a white draw margin which patched the dark UI).
        var transparentBg = new SolidColorPaint(SKColors.Transparent);
        HistogramChart.Background = System.Windows.Media.Brushes.Transparent;
        StatsSummaryChart.Background = System.Windows.Media.Brushes.Transparent;
        PerQuestionChart.Background = System.Windows.Media.Brushes.Transparent;
        HistogramChart.DrawMarginFrame = null;
        StatsSummaryChart.DrawMarginFrame = null;
        PerQuestionChart.DrawMarginFrame = null;
    }

    public void LoadStats(ExamModel exam, IEnumerable<Submission> submissions)
    {
        var subs = submissions.ToList();
        int totalPts = exam.TotalPoints;

        // Theme colors resolved from /Themes/Colors.xaml at runtime so live branding still applies.
        var primary = ThemeColor("Accent.Primary",  SKColors.SteelBlue);
        var success = ThemeColor("Accent.Success",  SKColors.SeaGreen);
        var danger  = ThemeColor("Accent.Danger",   SKColors.OrangeRed);
        var textCol = ThemeColor("Text.Secondary",  SKColor.Parse("94A3B8"));

        // Compute per-submission percentage scores
        var pcts = new List<double>();
        foreach (var sub in subs)
        {
            int score = 0;
            foreach (var q in exam.Questions)
            {
                var a = sub.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                if (a != null && IsCorrect(q, a)) score += q.Points;
            }
            pcts.Add(totalPts > 0 ? score * 100.0 / totalPts : 0);
        }

        SummaryText.Text = subs.Count == 0
            ? "No submissions yet."
            : $"Submissions: {subs.Count}    Avg: {pcts.Average():F1}%    Max: {pcts.Max():F1}%    Min: {pcts.Min():F1}%";

        // ── Chart 1: Histogram of scores (10 buckets) ──
        var buckets = new int[10];
        foreach (var p in pcts)
        {
            int idx = (int)System.Math.Min(9, System.Math.Floor(p / 10));
            buckets[idx]++;
        }
        HistogramChart.Series = new ISeries[]
        {
            new ColumnSeries<int>
            {
                Values = buckets,
                Name = "Students",
                Fill = new SolidColorPaint(primary),
            }
        };
        HistogramChart.XAxes = new[]
        {
            new Axis
            {
                Labels = new[] { "0-10", "11-20", "21-30", "31-40", "41-50", "51-60", "61-70", "71-80", "81-90", "91-100" },
                Name = "Score range (%)",
                LabelsPaint = new SolidColorPaint(textCol),
                NamePaint = new SolidColorPaint(textCol),
            }
        };
        HistogramChart.YAxes = new[]
        {
            new Axis
            {
                Name = "Students",
                MinLimit = 0,
                LabelsPaint = new SolidColorPaint(textCol),
                NamePaint = new SolidColorPaint(textCol),
            }
        };
        HistogramChart.LegendPosition = LegendPosition.Bottom;
        HistogramChart.LegendTextPaint = new SolidColorPaint(textCol);

        // ── Chart 2: Avg / Min / Max ──
        if (pcts.Count > 0)
        {
            StatsSummaryChart.Series = new ISeries[]
            {
                new ColumnSeries<double>
                {
                    Values = new[] { pcts.Average(), pcts.Min(), pcts.Max() },
                    Name = "Score (%)",
                    Fill = new SolidColorPaint(primary),
                }
            };
            StatsSummaryChart.XAxes = new[]
            {
                new Axis
                {
                    Labels = new[] { "Average", "Min", "Max" },
                    LabelsPaint = new SolidColorPaint(textCol),
                }
            };
            StatsSummaryChart.YAxes = new[]
            {
                new Axis
                {
                    Name = "Score (%)",
                    MinLimit = 0,
                    MaxLimit = 100,
                    Labeler = v => $"{v:F0}%",
                    LabelsPaint = new SolidColorPaint(textCol),
                    NamePaint = new SolidColorPaint(textCol),
                }
            };
            StatsSummaryChart.LegendPosition = LegendPosition.Bottom;
            StatsSummaryChart.LegendTextPaint = new SolidColorPaint(textCol);
        }

        // ── Chart 3: Per-question correct rate — green if >=50%, red if below ──
        var perQValues = new List<double>();
        var qLabels = new List<string>();
        for (int i = 0; i < exam.Questions.Count; i++)
        {
            var q = exam.Questions[i];
            int correctCount = subs.Count(sub =>
            {
                var a = sub.Answers.FirstOrDefault(x => x.QuestionId == q.Id);
                return a != null && IsCorrect(q, a);
            });
            double rate = subs.Count > 0 ? correctCount * 100.0 / subs.Count : 0;
            perQValues.Add(rate);
            qLabels.Add($"Q{i + 1}");
        }

        // Two overlay series: low (red) and high (green) — each entry NaN where the other owns it.
        var lowValues = perQValues.Select(v => v < 50 ? v : double.NaN).ToArray();
        var highValues = perQValues.Select(v => v >= 50 ? v : double.NaN).ToArray();

        PerQuestionChart.Series = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values = highValues,
                Name = "Correct ≥ 50%",
                Fill = new SolidColorPaint(success),
            },
            new ColumnSeries<double>
            {
                Values = lowValues,
                Name = "Correct < 50%",
                Fill = new SolidColorPaint(danger),
            }
        };
        PerQuestionChart.XAxes = new[]
        {
            new Axis
            {
                Labels = qLabels.ToArray(),
                LabelsPaint = new SolidColorPaint(textCol),
            }
        };
        PerQuestionChart.YAxes = new[]
        {
            new Axis
            {
                Name = "Correct Rate (%)",
                MinLimit = 0,
                MaxLimit = 100,
                Labeler = v => $"{v:F0}%",
                LabelsPaint = new SolidColorPaint(textCol),
                NamePaint = new SolidColorPaint(textCol),
            }
        };
        PerQuestionChart.LegendPosition = LegendPosition.Bottom;
        PerQuestionChart.LegendTextPaint = new SolidColorPaint(textCol);
    }

    /// <summary>
    /// Pull a Color from <see cref="Application.Resources"/> by design-system key
    /// (e.g. "Accent.Primary") and convert to SKColor. Falls back to <paramref name="fallback"/>
    /// if the key is missing or the value isn't a SolidColorBrush.
    /// </summary>
    private static SKColor ThemeColor(string key, SKColor fallback)
    {
        try
        {
            if (Application.Current?.Resources[key] is SolidColorBrush brush)
            {
                var c = brush.Color;
                return new SKColor(c.R, c.G, c.B, c.A);
            }
        }
        catch { }
        return fallback;
    }

    private static bool IsCorrect(Question q, Answer a)
    {
        if (q.CorrectChoiceLabels == null || q.CorrectChoiceLabels.Count == 0) return false;
        var correct = q.CorrectChoiceLabels.ToHashSet();
        var picked = a.SelectedLabels?.ToHashSet() ?? new HashSet<string>();
        return picked.SetEquals(correct);
    }
}
