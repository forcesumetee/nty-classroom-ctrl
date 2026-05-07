using ClassroomCtrl.Exam.Shared;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 13: Persists quizzes (Exams) to %ProgramData%\NTY\ClassroomCtrl\Quizzes\{id}.json
/// and exam-session results to ...\ExamResults\{sessionId}.json.
///
/// Holds in-memory current session state (one active exam at a time per teacher PC).
/// Subscribes to ControlServer.QuizSubmissionReceived to collect submissions.
/// </summary>
public class ExamService
{
    public static readonly string QuizzesFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NTY", "ClassroomCtrl", "Quizzes");

    public static readonly string ResultsFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NTY", "ClassroomCtrl", "ExamResults");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public ObservableCollection<ExamModel> Quizzes { get; } = new();

    /// <summary>Active session — null if no exam in progress.</summary>
    public Guid? CurrentSessionId { get; private set; }
    public ExamModel? CurrentExam { get; private set; }
    public ObservableCollection<Submission> CurrentSubmissions { get; } = new();

    public event EventHandler? SubmissionsChanged;

    public ExamService()
    {
        try { Directory.CreateDirectory(QuizzesFolder); } catch { }
        try { Directory.CreateDirectory(ResultsFolder); } catch { }
        LoadQuizzes();
    }

    public void LoadQuizzes()
    {
        Quizzes.Clear();
        if (!Directory.Exists(QuizzesFolder)) return;
        foreach (var path in Directory.GetFiles(QuizzesFolder, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(path);
                var exam = JsonSerializer.Deserialize<ExamModel>(json, JsonOpts);
                if (exam != null) Quizzes.Add(exam);
            }
            catch { /* skip corrupted */ }
        }
    }

    public void SaveQuiz(ExamModel exam)
    {
        try { Directory.CreateDirectory(QuizzesFolder); } catch { }
        var path = Path.Combine(QuizzesFolder, exam.Id.ToString("N") + ".json");
        var json = JsonSerializer.Serialize(exam, JsonOpts);
        File.WriteAllText(path, json);

        var existing = Quizzes.FirstOrDefault(q => q.Id == exam.Id);
        if (existing != null)
        {
            int idx = Quizzes.IndexOf(existing);
            Quizzes[idx] = exam;
        }
        else
        {
            Quizzes.Add(exam);
        }
    }

    public void DeleteQuiz(Guid id)
    {
        var path = Path.Combine(QuizzesFolder, id.ToString("N") + ".json");
        try { if (File.Exists(path)) File.Delete(path); } catch { }
        var existing = Quizzes.FirstOrDefault(q => q.Id == id);
        if (existing != null) Quizzes.Remove(existing);
    }

    public Guid StartSession(ExamModel exam)
    {
        CurrentSessionId = Guid.NewGuid();
        CurrentExam = exam;
        CurrentSubmissions.Clear();
        SubmissionsChanged?.Invoke(this, EventArgs.Empty);
        return CurrentSessionId.Value;
    }

    public void EndSession()
    {
        if (CurrentSessionId == null || CurrentExam == null) return;
        try
        {
            var path = Path.Combine(ResultsFolder, CurrentSessionId.Value.ToString("N") + ".json");
            var bundle = new SessionResults
            {
                SessionId = CurrentSessionId.Value,
                Exam = CurrentExam,
                Submissions = CurrentSubmissions.ToList(),
                EndedAtUtc = DateTime.UtcNow,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(bundle, JsonOpts));
        }
        catch { }

        CurrentSessionId = null;
        CurrentExam = null;
    }

    /// <summary>Wired up in App.xaml.cs from Server.QuizSubmissionReceived.</summary>
    public void HandleSubmission(QuizAnswerSubmitPayload payload)
    {
        if (CurrentSessionId == null || CurrentExam == null) return;
        if (payload.SessionId != CurrentSessionId) return;
        if (CurrentSubmissions.Any(s => s.StudentEndpointId == payload.StudentEndpointId)) return;

        var graded = GradingService.Grade(CurrentExam, payload);
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            CurrentSubmissions.Add(graded);
            SubmissionsChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    public class SessionResults
    {
        public Guid SessionId { get; set; }
        public ExamModel Exam { get; set; } = new();
        public List<Submission> Submissions { get; set; } = new();
        public DateTime EndedAtUtc { get; set; }
    }
}
