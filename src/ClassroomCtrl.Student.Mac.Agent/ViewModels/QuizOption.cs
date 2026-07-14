using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassroomCtrl.Student.Mac.Agent.ViewModels;

/// <summary>One selectable quiz option. <see cref="IsSelected"/> is two-way bound to a RadioButton
/// (single-choice group), so the group's mutual exclusion keeps exactly one selected.</summary>
public sealed partial class QuizOption : ObservableObject
{
    public QuizOption(int index, string text)
    {
        Index = index;
        Text = text;
    }

    public int Index { get; }
    public string Text { get; }

    [ObservableProperty] private bool isSelected;
}
