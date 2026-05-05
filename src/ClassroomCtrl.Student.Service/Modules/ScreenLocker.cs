using Microsoft.Extensions.Logging;

namespace ClassroomCtrl.Student.Service.Modules;

/// <summary>
/// Screen-lock module — implementation per Spec §6.3.
/// Skeleton/stub. The dev team should implement using:
///   - user32!BlockInput(TRUE/FALSE) for input blocking
///   - Topmost full-screen black overlay window per monitor (one per Screen.AllScreens)
///   - Note: Ctrl+Alt+Del cannot be intercepted by design — document this in user manual.
/// PDPA compliance: when locked, an on-screen message identifies the school
/// and the supervising teacher (Spec §1.4).
/// </summary>
public class ScreenLocker
{
    private readonly ILogger<ScreenLocker> _logger;
    private bool _isLocked;

    public ScreenLocker(ILogger<ScreenLocker> logger) => _logger = logger;

    public void Lock()
    {
        if (_isLocked) return;
        _logger.LogInformation("Lock requested by teacher");
        // TODO §6.3: BlockInput(true) + spawn overlay windows
        _isLocked = true;
    }

    public void Unlock()
    {
        if (!_isLocked) return;
        _logger.LogInformation("Unlock requested by teacher");
        // TODO §6.3: BlockInput(false) + close overlays
        _isLocked = false;
    }
}
