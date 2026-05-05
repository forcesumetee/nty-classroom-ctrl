using System.Windows;

namespace ClassroomCtrl.Student.Agent;

public partial class RemoteControlBanner : Window
{
    public RemoteControlBanner()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
            Top = 0;
        };
    }
}
