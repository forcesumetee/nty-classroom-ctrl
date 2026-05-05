using ClassroomCtrl.Exam.Shared;
using ClassroomCtrl.Shared.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomCtrl.Teacher.Quiz;

public partial class QuestionEditorDialog : Window
{
    private readonly Question _q;
    private readonly ObservableCollection<ChoiceVM> _choices = new();

    public QuestionEditorDialog(Question q)
    {
        InitializeComponent();
        _q = q;
        ChoicesItems.ItemsSource = _choices;

        // Initialize fields
        QuestionTextBox.Text = q.Text;
        PointsBox.Text = q.Points.ToString();
        SelectTypeInCombo(q.Type);

        // Default for new questions: 4 empty choices for MC
        if (q.Type == QuestionType.McqSingle && q.Choices.Count == 0)
        {
            for (int i = 0; i < 4; i++)
            {
                _choices.Add(new ChoiceVM { Label = ((char)('A' + i)).ToString(), Text = "", IsCorrect = false });
            }
        }
        else
        {
            foreach (var c in q.Choices)
            {
                _choices.Add(new ChoiceVM
                {
                    Label = c.Label,
                    Text = c.Text,
                    IsCorrect = q.CorrectChoiceLabels.Contains(c.Label, StringComparer.OrdinalIgnoreCase),
                });
            }
        }

        if (q.Type == QuestionType.TrueFalse) EnsureTrueFalseChoices();
        if (q.Type == QuestionType.ShortAnswer) ShortAnswerBox.Text = q.CorrectShortAnswer;

        UpdatePanelVisibility();
    }

    private void SelectTypeInCombo(QuestionType type)
    {
        var tag = type switch
        {
            QuestionType.TrueFalse => "TrueFalse",
            QuestionType.ShortAnswer => "ShortAnswer",
            _ => "McqSingle",
        };
        foreach (var item in TypeBox.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag as string) == tag) { TypeBox.SelectedItem = item; return; }
        }
        TypeBox.SelectedIndex = 0;
    }

    private QuestionType GetSelectedType()
    {
        var tag = (TypeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        return tag switch
        {
            "TrueFalse" => QuestionType.TrueFalse,
            "ShortAnswer" => QuestionType.ShortAnswer,
            _ => QuestionType.McqSingle,
        };
    }

    private void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        var type = GetSelectedType();
        if (type == QuestionType.TrueFalse) EnsureTrueFalseChoices();
        else if (type == QuestionType.McqSingle && _choices.Count == 0)
        {
            for (int i = 0; i < 4; i++)
                _choices.Add(new ChoiceVM { Label = ((char)('A' + i)).ToString(), Text = "", IsCorrect = false });
        }
        UpdatePanelVisibility();
    }

    private void UpdatePanelVisibility()
    {
        var type = GetSelectedType();
        ChoicesGrid.Visibility = type == QuestionType.ShortAnswer ? Visibility.Collapsed : Visibility.Visible;
        ShortAnswerPanel.Visibility = type == QuestionType.ShortAnswer ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EnsureTrueFalseChoices()
    {
        // Reset to two fixed choices
        var existingCorrect = _choices.FirstOrDefault(c => c.IsCorrect);
        var correctLabel = existingCorrect?.Label ?? "T";
        _choices.Clear();
        _choices.Add(new ChoiceVM { Label = "T", Text = Loc.Get("Lbl_True"), IsCorrect = correctLabel == "T" });
        _choices.Add(new ChoiceVM { Label = "F", Text = Loc.Get("Lbl_False"), IsCorrect = correctLabel == "F" });
    }

    private void AddChoice_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedType() == QuestionType.TrueFalse) return;
        var label = ((char)('A' + _choices.Count)).ToString();
        _choices.Add(new ChoiceVM { Label = label, Text = "", IsCorrect = false });
    }

    private void RemoveChoiceRow_Click(object sender, RoutedEventArgs e)
    {
        if (GetSelectedType() == QuestionType.TrueFalse) return;
        if (sender is System.Windows.Controls.Button btn && btn.Tag is ChoiceVM c)
        {
            _choices.Remove(c);
            // Relabel remaining
            for (int i = 0; i < _choices.Count; i++)
                _choices[i].Label = ((char)('A' + i)).ToString();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var type = GetSelectedType();

        if (string.IsNullOrWhiteSpace(QuestionTextBox.Text))
        {
            MessageBox.Show(Loc.Get("Err_QuestionEmpty"));
            return;
        }

        _q.Type = type;
        _q.Text = QuestionTextBox.Text.Trim();
        if (int.TryParse(PointsBox.Text, out int p) && p > 0) _q.Points = p; else _q.Points = 1;

        if (type == QuestionType.ShortAnswer)
        {
            _q.CorrectShortAnswer = ShortAnswerBox.Text?.Trim() ?? "";
            if (_q.CorrectShortAnswer.Length == 0)
            {
                MessageBox.Show(Loc.Get("Err_NoCorrectAnswer"));
                return;
            }
            _q.Choices.Clear();
            _q.CorrectChoiceLabels.Clear();
        }
        else
        {
            _q.Choices = _choices
                .Where(c => !string.IsNullOrWhiteSpace(c.Text) || type == QuestionType.TrueFalse)
                .Select(c => new Choice { Label = c.Label, Text = c.Text })
                .ToList();
            _q.CorrectChoiceLabels = _choices
                .Where(c => c.IsCorrect)
                .Select(c => c.Label)
                .ToList();
            if (_q.CorrectChoiceLabels.Count == 0)
            {
                MessageBox.Show(Loc.Get("Err_NoCorrectAnswer"));
                return;
            }
            if (_q.Choices.Count < 2)
            {
                MessageBox.Show(Loc.Get("Err_TooFewChoices"));
                return;
            }
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    public partial class ChoiceVM : ObservableObject
    {
        [ObservableProperty] private string label = "";
        [ObservableProperty] private string text = "";
        [ObservableProperty] private bool isCorrect;
    }
}
