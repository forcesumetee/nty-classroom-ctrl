using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-10-B — the teacher "Talk to Class" broadcast seam (same passthrough shape as
/// ITeacherScreenSink). Start/Stop route RELIABLE (bug #7 — a dropped Stop leaves every
/// student's playback session open); frames ride the dedicated lossy-class audio channel.
/// Both channel decisions live inside ControlServer — the seam just forwards.
/// </summary>
public interface ITeacherAudioSink
{
    Task BroadcastAudioStreamControlAsync(bool start, CancellationToken ct);
    Task BroadcastAudioFrameAsync(AudioStreamFrameMessage frame, CancellationToken ct);
}
