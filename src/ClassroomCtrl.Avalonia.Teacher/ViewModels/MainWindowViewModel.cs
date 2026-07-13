using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-2-D — the window VM: the student grid + the status affordances (the listen
/// address so you know what IP to point a student at, the connected count, and the
/// empty-state flag). Recomputes on every grid change (which arrive on the UI thread
/// — the grid marshals its mutations, so this handler is UI-thread-safe).
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    public TeacherGridViewModel Grid { get; }

    [ObservableProperty] private string listenAddress;
    [ObservableProperty] private string statusText = "No students connected";
    [ObservableProperty] private bool isEmpty = true;

    public MainWindowViewModel(TeacherGridViewModel grid, string listenAddress)
    {
        Grid = grid;
        this.listenAddress = listenAddress;
        Grid.Students.CollectionChanged += OnStudentsChanged;
        Refresh();
    }

    private void OnStudentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        int n = Grid.Students.Count;
        IsEmpty = n == 0;
        StatusText = n switch
        {
            0 => "No students connected",
            1 => "1 student connected",
            _ => $"{n} students connected",
        };
    }
}
