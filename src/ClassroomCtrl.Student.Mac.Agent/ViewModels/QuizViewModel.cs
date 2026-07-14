using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ClassroomCtrl.Shared.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Student.Mac.Agent.ViewModels;

/// <summary>
/// Backing state for the QuizWindow: the question, its options, and a submit-once interaction. Identity-free —
/// on submit it raises <see cref="AnswerSubmitted"/> so the App relays the choice to the daemon over IPC (the
/// daemon stamps the real student identity). All mutation happens on the UI thread (the App marshals the
/// incoming quiz via Dispatcher before calling <see cref="Load"/>).
/// </summary>
public sealed partial class QuizViewModel : ObservableObject
{
    /// <summary>Raised once when the student submits (button then disables to block a double-send).</summary>
    public event Action<QuizSubmitRequestMessage>? AnswerSubmitted;

    public Guid QuizId { get; private set; }

    [ObservableProperty] private string question = "";
    public ObservableCollection<QuizOption> Options { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SubmitCommand))]
    private bool hasSubmitted;

    /// <summary>Populate from an incoming quiz, resetting any prior state.</summary>
    public void Load(QuizQuestionMessage q)
    {
        QuizId = q.QuizId;
        Question = q.Question;
        HasSubmitted = false;

        foreach (var o in Options) o.PropertyChanged -= OnOptionChanged;
        Options.Clear();
        for (int i = 0; i < q.Options.Count; i++)
        {
            var opt = new QuizOption(i, q.Options[i]);
            opt.PropertyChanged += OnOptionChanged;
            Options.Add(opt);
        }
        SubmitCommand.NotifyCanExecuteChanged();
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QuizOption.IsSelected)) SubmitCommand.NotifyCanExecuteChanged();
    }

    private QuizOption? Selected => Options.FirstOrDefault(o => o.IsSelected);
    private bool CanSubmit() => !HasSubmitted && Selected is not null;

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private void Submit()
    {
        var sel = Selected;
        if (sel is null || HasSubmitted) return;
        HasSubmitted = true;   // disables the command (submit-once)
        AnswerSubmitted?.Invoke(new QuizSubmitRequestMessage
        {
            QuizId = QuizId,
            SelectedIndex = sel.Index,
            SelectedText = sel.Text,
        });
    }
}
