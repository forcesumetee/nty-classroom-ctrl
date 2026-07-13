using System;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-3-B — the seam the screen-view UI depends on, so a <c>ScreenViewModel</c> can
/// be unit-tested against a fake source with no real transport.
/// <see cref="TeacherSession"/> implements it by forwarding to the in-app
/// <c>ControlServer</c>; the frame flow itself was already ported in TT-1-C
/// (ControlServer.StudentStreamFrameReceived / RequestStudentStreamAsync /
/// StopStudentStreamAsync), so this interface is pure structural re-exposure.
/// </summary>
public interface IStudentStreamSource
{
    /// <summary>Raised — on a transport background read loop — for every screen frame
    /// received from ANY streaming student. Consumers MUST filter by
    /// <c>StudentId</c> and marshal any UI mutation onto the UI thread.</summary>
    event EventHandler<(Guid StudentId, ScreenStreamFrameMessage Frame)>? StudentStreamFrameReceived;

    /// <summary>Ask one student to start streaming their screen in the given codec.</summary>
    Task RequestStudentStreamAsync(Guid studentId, VideoCodec codec, CancellationToken ct);

    /// <summary>Ask one student to stop streaming their screen.</summary>
    Task StopStudentStreamAsync(Guid studentId, CancellationToken ct);
}
