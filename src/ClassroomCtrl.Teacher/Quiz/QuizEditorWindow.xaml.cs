using ClassroomCtrl.Exam.Shared;
using ClassroomCtrl.Shared.Localization;
using System.Collections.ObjectModel;
using System.Windows;
using ExamModel = ClassroomCtrl.Exam.Shared.Exam;

namespace ClassroomCtrl.Teacher.Quiz;

public partial class QuizEditorWindow : Window
{
    private readonly ExamModel _exam;
    private readonly ObservableCollection<Question> _questions;

    public QuizEditorWindow(ExamModel exam)
    {
        InitializeComponent();
        _exam = exam;
        _questions = new ObservableCollection<Question>(exam.Questions);
        QuestionListBox.ItemsSource = _questions;

        TitleBox.Text = exam.Title;
        DescriptionBox.Text = exam.Description;
        DurationBox.Text = exam.TimeLimitMinutes.ToString();
        ShowScoreBox.IsChecked = exam.ShowScoreToStudent;
    }

    private Question? Selected => QuestionListBox.SelectedItem as Question;

    private void AddQuestion_Click(object sender, RoutedEventArgs e)
    {
        var q = new Question { Type = QuestionType.McqSingle, Text = "", Points = 1, Order = _questions.Count };
        var dlg = new QuestionEditorDialog(q) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _questions.Add(q);
        }
    }

    private void EditQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        var dlg = new QuestionEditorDialog(Selected) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            // Trigger refresh
            int idx = _questions.IndexOf(Selected);
            var q = Selected;
            _questions.RemoveAt(idx);
            _questions.Insert(idx, q);
            QuestionListBox.SelectedItem = q;
        }
    }

    private void QuestionListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Selected != null) EditQuestion_Click(sender, e);
    }

    private void RemoveQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (Selected == null) return;
        _questions.Remove(Selected);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(TitleBox.Text))
        {
            MessageBox.Show(Loc.Get("Err_QuizNoTitle"));
            return;
        }
        if (_questions.Count == 0)
        {
            MessageBox.Show(Loc.Get("Err_NoQuestions"));
            return;
        }

        _exam.Title = TitleBox.Text.Trim();
        _exam.Description = DescriptionBox.Text?.Trim() ?? "";
        _exam.ShowScoreToStudent = ShowScoreBox.IsChecked == true;
        if (int.TryParse(DurationBox.Text, out int dur) && dur > 0) _exam.TimeLimitMinutes = dur;
        _exam.Questions = _questions.ToList();
        for (int i = 0; i < _exam.Questions.Count; i++) _exam.Questions[i].Order = i;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
