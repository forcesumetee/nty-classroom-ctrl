using ClassroomCtrl.Exam.Shared;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Teacher.Services;
using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;

namespace ClassroomCtrl.Teacher.Quiz;

public partial class QuizManagerWindow : Window
{
    public QuizManagerWindow()
    {
        InitializeComponent();
        if (App.Exam != null)
        {
            QuizListBox.ItemsSource = App.Exam.Quizzes;
        }
    }

    private ExamModel? Selected => QuizListBox.SelectedItem as ExamModel;

    private void QuizListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var q = Selected;
        if (q == null)
        {
            PreviewTitle.Text = "";
            PreviewMeta.Text = "";
            PreviewDesc.Text = "";
            PreviewQuestions.ItemsSource = null;
            return;
        }
        PreviewTitle.Text = q.Title;
        PreviewMeta.Text = $"{q.Questions.Count} questions · {q.TimeLimitMinutes} min · {q.TotalPoints} pts";
        PreviewDesc.Text = q.Description;
        PreviewQuestions.ItemsSource = q.Questions;
    }

    private void NewQuiz_Click(object sender, RoutedEventArgs e)
    {
        var newQuiz = new ExamModel { Title = "New Quiz", TimeLimitMinutes = 15 };
        var editor = new QuizEditorWindow(newQuiz) { Owner = this };
        if (editor.ShowDialog() == true && App.Exam != null)
        {
            App.Exam.SaveQuiz(newQuiz);
            RefreshQuizList(newQuiz.Id);
        }
    }

    private void EditQuiz_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null || App.Exam == null) return;
        var keepId = Selected.Id;
        var editor = new QuizEditorWindow(Selected) { Owner = this };
        if (editor.ShowDialog() == true)
        {
            App.Exam.SaveQuiz(Selected);
            RefreshQuizList(keepId);
        }
    }

    private void DeleteQuiz_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null || App.Exam == null) return;
        var confirm = MessageBox.Show(
            Loc.Format("Confirm_DeleteQuiz", Selected.Title),
            Loc.Get("Lbl_QuizManager"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        App.Exam.DeleteQuiz(Selected.Id);
        RefreshQuizList();
    }

    /// <summary>
    /// Bug #4 fix: Exam is a plain POCO (no INotifyPropertyChanged), and EditQuiz reuses
    /// the same Exam reference, so ObservableCollection's Replace event fires with
    /// Old==New and the ListBoxItem never rebuilds. We force a full reload from disk so
    /// every row is a fresh deserialized object — bindings re-evaluate against new values.
    /// </summary>
    private void RefreshQuizList(System.Guid? selectId = null)
    {
        if (App.Exam == null) return;
        App.Exam.LoadQuizzes(); // Clear() + reload from %ProgramData%\NTY\ClassroomCtrl\Quizzes
        if (selectId.HasValue)
        {
            QuizListBox.SelectedItem = App.Exam.Quizzes
                .FirstOrDefault(q => q.Id == selectId.Value);
        }
        QuizListBox_SelectionChanged(null!, null!); // refresh preview pane
    }

    private void ImportWord_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.Get("Btn_ImportWord"),
            Filter = "Word documents (*.docx)|*.docx",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var defaultTitle = Path.GetFileNameWithoutExtension(dlg.FileName);
            var imported = WordImportService.ImportFromDocx(dlg.FileName, defaultTitle);
            if (imported.Questions.Count == 0)
            {
                MessageBox.Show(Loc.Get("Err_NoQuestions"), Loc.Get("Btn_ImportWord"));
                return;
            }
            StatusText.Text = Loc.Format("Msg_Imported", imported.Questions.Count);
            var editor = new QuizEditorWindow(imported) { Owner = this };
            if (editor.ShowDialog() == true && App.Exam != null)
            {
                App.Exam.SaveQuiz(imported);
                RefreshQuizList(imported.Id);
            }
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", Loc.Get("Btn_ImportWord"));
        }
    }

    private async void SendQuiz_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        if (Selected.Questions.Count == 0)
        {
            MessageBox.Show(Loc.Get("Err_NoQuestions"));
            return;
        }
        if (App.Server == null || App.Exam == null) return;

        var confirm = MessageBox.Show(
            Loc.Format("Confirm_SendQuiz", Selected.Title, Selected.TimeLimitMinutes),
            Loc.Get("Lbl_QuizManager"),
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var sessionId = App.Exam.StartSession(Selected);
        try
        {
            await App.Server.BroadcastQuizStartAsync(Selected, sessionId, System.Threading.CancellationToken.None);
            StatusText.Text = Loc.Format("Msg_QuizSent", Selected.Title);

            // Auto-open results window so teacher sees submissions roll in.
            var results = new ExamResultsWindow(Selected) { Owner = this };
            results.Show();
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}", Loc.Get("Lbl_QuizManager"));
        }
    }

    private void ViewResults_Click(object sender, RoutedEventArgs e)
    {
        if (App.Exam?.CurrentExam == null)
        {
            MessageBox.Show(Loc.Get("Err_NoActiveSession"));
            return;
        }
        var results = new ExamResultsWindow(App.Exam.CurrentExam) { Owner = this };
        results.Show();
    }

    private async void EndExam_Click(object sender, RoutedEventArgs e)
    {
        if (App.Exam?.CurrentSessionId == null || App.Server == null)
        {
            MessageBox.Show(Loc.Get("Err_NoActiveSession"));
            return;
        }
        var confirm = MessageBox.Show(
            Loc.Get("Confirm_EndExam"),
            Loc.Get("Lbl_QuizManager"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var sid = App.Exam.CurrentSessionId.Value;
        var showResults = App.Exam.CurrentExam?.ShowScoreToStudent ?? true;
        try
        {
            await App.Server.BroadcastQuizEndAsync(sid, showResults, System.Threading.CancellationToken.None);
            App.Exam.EndSession();
            StatusText.Text = Loc.Get("Msg_ExamEnded");
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"Error: {ex.Message}");
        }
    }
}
