using ClassroomCtrl.Shared.Models;

namespace ClassroomCtrl.Teacher.Services;

public class BreakoutManager
{
    private List<BreakoutRoom> _rooms = new();
    public IReadOnlyList<BreakoutRoom> Rooms => _rooms;

    public List<BreakoutRoom> CreateRandom(IList<Guid> studentIds, int roomCount) =>
        _rooms = BreakoutRoom.AssignRandomly(studentIds, roomCount);

    public BreakoutRoom CreateManual(string name)
    {
        var r = new BreakoutRoom { Name = name };
        _rooms.Add(r);
        return r;
    }

    public void AssignToRoom(Guid roomId, Guid studentId)
    {
        // remove from any current room first
        foreach (var r in _rooms) r.MemberIds.Remove(studentId);
        var target = _rooms.FirstOrDefault(r => r.Id == roomId);
        target?.MemberIds.Add(studentId);
    }

    public void SetHost(Guid roomId, Guid? hostId)
    {
        var r = _rooms.FirstOrDefault(x => x.Id == roomId);
        if (r != null) r.HostId = hostId;
    }

    public void Dissolve() => _rooms.Clear();
}
