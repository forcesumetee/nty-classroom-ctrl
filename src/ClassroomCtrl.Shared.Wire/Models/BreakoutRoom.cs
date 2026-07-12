namespace ClassroomCtrl.Shared.Models;

public class BreakoutRoom
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public List<Guid> MemberIds { get; } = new();
    public Guid? HostId { get; set; }

    public static List<BreakoutRoom> AssignRandomly(IList<Guid> students, int roomCount, Func<string, string>? namer = null)
    {
        if (roomCount <= 0) throw new ArgumentException("roomCount must be > 0");
        var shuffled = students.OrderBy(_ => Random.Shared.Next()).ToList();
        int baseSize = shuffled.Count / roomCount;
        int remainder = shuffled.Count % roomCount;

        var rooms = new List<BreakoutRoom>();
        int idx = 0;
        for (int i = 0; i < roomCount; i++)
        {
            int size = baseSize + (i < remainder ? 1 : 0);
            var room = new BreakoutRoom { Name = namer?.Invoke($"Room {i + 1}") ?? $"Room {i + 1}" };
            for (int j = 0; j < size && idx < shuffled.Count; j++, idx++)
                room.MemberIds.Add(shuffled[idx]);
            rooms.Add(room);
        }
        return rooms;
    }
}
