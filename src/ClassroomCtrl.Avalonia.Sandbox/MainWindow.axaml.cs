using Avalonia.Controls;
using ClassroomCtrl.Avalonia.Sandbox.ViewModels;

namespace ClassroomCtrl.Avalonia.Sandbox;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainWindowViewModel();
    }
}