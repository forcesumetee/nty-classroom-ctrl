namespace ClassroomCtrl.Shared.Models;

public record PolicySet(
    bool BlockUsbStorage = false,
    bool BlockOpticalDrive = false,
    bool BlockPrinting = false,
    List<string>? BlockedProcessNames = null,
    List<string>? BlockedHostnames = null)
{
    public static PolicySet None => new();
}
