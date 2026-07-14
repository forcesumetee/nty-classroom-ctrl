using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-8-C — the teacher-screen-broadcast seam (Start/Stop control + per-frame). TeacherSession
/// implements it (passthrough to the already-ported ControlServer sends); TeacherScreenBroadcaster
/// pushes captured frames to it. Frames go LOSSY; the STOP goes reliable (bug #5) — both handled
/// inside ControlServer.BroadcastScreenStreamControlAsync / BroadcastScreenFrameAsync.
/// </summary>
public interface ITeacherScreenSink
{
    Task BroadcastScreenStreamControlAsync(bool start, CancellationToken ct);
    Task BroadcastScreenFrameAsync(ScreenStreamFrameMessage frame, CancellationToken ct);
}
