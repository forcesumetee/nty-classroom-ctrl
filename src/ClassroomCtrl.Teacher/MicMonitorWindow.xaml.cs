using ClassroomCtrl.Teacher.ViewModels;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 4.6: Discord-style mic monitoring. Per-row Listen toggle starts a
/// MicMonitorStart targeted to that student so they begin always-on talkback.
/// Incoming StudentAudioStreamFrame frames mix through the existing
/// StudentAudioMixer at App.StudentAudioMixer.
/// </summary>
public partial class MicMonitorWindow : Window
{
    private readonly HashSet<Guid> _listening = new();

    public MicMonitorWindow()
    {
        InitializeComponent();
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel vm)
        {
            StudentList.ItemsSource = vm.Students;
        }
    }

    private async void ToggleListen_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        if (sender is not Button b || b.Tag is not StudentViewModel s) return;

        if (_listening.Contains(s.EndpointId))
        {
            await App.Server.SendMicMonitorStopAsync(s.EndpointId, CancellationToken.None);
            _listening.Remove(s.EndpointId);
            b.Content = "🎙 Toggle";
        }
        else
        {
            await App.Server.SendMicMonitorStartAsync(s.EndpointId, CancellationToken.None);
            _listening.Add(s.EndpointId);
            b.Content = "🔊 Listening";
        }
    }
}
