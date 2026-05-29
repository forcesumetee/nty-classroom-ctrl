using System;
using System.Management;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 14-B (Tier 1) — watches for webcam device-arrival / removal events
/// so the agent can emit <see cref="ClassroomCtrl.Shared.Protocol.WebcamStateUpdateMessage"/>
/// at startup and on every change.  Drives the teacher's per-student
/// "has-cam" indicator + the Tier 1 acceptance "cam plugged mid-session
/// triggers UI refresh" check.
///
/// Implementation: <c>Win32_PnPEntity</c> WMI query for the two ClassGuid
/// values that PnP uses for camera devices on Windows (one modern, one
/// legacy image-class fallback).  Watches <c>Win32_DeviceChangeEvent</c>
/// for arrival/removal pulses; on each pulse re-enumerates + fires
/// <see cref="DeviceChanged"/> only when presence actually flips.
///
/// Why not AForge.Video.DirectShow's FilterInfoCollection here? Tier 1 does
/// not capture on the student side; pulling AForge in just for an enum is
/// wasteful and adds a binary-size hit to every student install.  Tier 2's
/// <c>StudentCameraBroadcaster</c> adds AForge and supersedes this watcher
/// (which can stay for the device-change signal even after Tier 2 lands).
/// </summary>
public class WebcamDeviceWatcher : IDisposable
{
    /// <summary>True if at least one camera-class PnP device is currently
    /// enumerable.  Refreshed on every WMI change event.  Safe to read
    /// from the UI thread.</summary>
    public bool DeviceAvailable { get; private set; }

    /// <summary>Fires when <see cref="DeviceAvailable"/> flips state.  Runs
    /// on the WMI background thread; subscribers must marshal to the UI
    /// thread if they touch WPF state.</summary>
    public event Action? DeviceChanged;

    private readonly ManagementEventWatcher? _watcher;
    private bool _disposed;

    public WebcamDeviceWatcher()
    {
        DeviceAvailable = EnumerateOnce();
        try
        {
            // Win32_DeviceChangeEvent EventType: 1=ConfigChanged, 2=DeviceArrival,
            // 3=DeviceRemoval, 4=Docking.  Watch arrival + removal only.
            var q = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3");
            _watcher = new ManagementEventWatcher(q);
            _watcher.EventArrived += OnDeviceChangeEvent;
            _watcher.Start();
        }
        catch
        {
            // WMI not available (very rare on Windows; possible on locked-down
            // VMs).  Watcher gracefully degrades to "static snapshot from
            // EnumerateOnce" — DeviceChanged never fires, but DeviceAvailable
            // is still correct at startup.
            _watcher = null;
        }
    }

    private void OnDeviceChangeEvent(object? sender, EventArrivedEventArgs e)
    {
        var was = DeviceAvailable;
        DeviceAvailable = EnumerateOnce();
        if (was != DeviceAvailable) DeviceChanged?.Invoke();
    }

    private static bool EnumerateOnce()
    {
        // Two PnP ClassGuid namespaces cover Windows webcams:
        //   {ca3e7ab9-b4c3-4ae6-8251-579ef933890f}  Camera class (Win10+, modern UVC)
        //   {6bdd1fc6-810f-11d0-bec7-08002be2092f}  Image class (legacy, some Win7-era drivers)
        // Query both; presence in either is sufficient.
        var queries = new[]
        {
            "SELECT Name FROM Win32_PnPEntity WHERE ClassGuid = '{ca3e7ab9-b4c3-4ae6-8251-579ef933890f}'",
            "SELECT Name FROM Win32_PnPEntity WHERE ClassGuid = '{6bdd1fc6-810f-11d0-bec7-08002be2092f}'",
        };
        foreach (var q in queries)
        {
            try
            {
                using var s = new ManagementObjectSearcher(q);
                using var c = s.Get();
                if (c.Count > 0) return true;
            }
            catch
            {
                // Permission or WMI service hiccup — ignore + try next query.
            }
        }
        return false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _watcher?.Stop(); } catch { }
        try { _watcher?.Dispose(); } catch { }
    }
}
