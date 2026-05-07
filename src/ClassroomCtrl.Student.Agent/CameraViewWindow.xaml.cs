using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClassroomCtrl.Student.Agent;

public partial class CameraViewWindow : Window
{
    public CameraViewWindow()
    {
        InitializeComponent();
        Left = SystemParameters.PrimaryScreenWidth - 500;
        Top = 60;
    }

    public void UpdateFrame(byte[] jpeg)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(jpeg);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            CamImage.Source = bmp;
        }
        catch { }
    }
}
