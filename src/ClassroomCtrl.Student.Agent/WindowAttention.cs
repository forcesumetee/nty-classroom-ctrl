using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 17.2 step 3 (Feature L) — Win32 helpers for the "LINE-style pop-to-
/// front" behavior on incoming teacher chat.  When the teacher messages a
/// student the student window should:
///   1. Come out of minimized state (if applicable)
///   2. Be brought to the foreground (focus)
///   3. Flash the taskbar button so the user notices even if focus-steal is
///      blocked by Windows (which it usually is when another app owns input)
///
/// Win32 reference: FlashWindowEx is the supported way to attract attention
/// without rude focus-stealing on Windows 10/11.  SetForegroundWindow is
/// rate-limited by Explorer to prevent app-stealing; the
/// Topmost-toggle + Activate combo is the established WPF workaround that
/// works inside the rules (Windows allows a foreground change immediately
/// after a UI event arrives).
///
/// Used by:
///   - MainWindow on incoming non-Conference Chat (Broadcast / DM / Room)
///   - ConferenceGalleryWindow on incoming Conference-context Chat
/// Both call <see cref="BringToFront"/> with their own window reference.
/// </summary>
internal static class WindowAttention
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    // FLASHW_ALL = FLASHW_CAPTION | FLASHW_TRAY — flash both the caption bar
    // and the taskbar button.  FLASHW_TIMERNOFG would keep flashing until the
    // window comes to the foreground; we use a fixed 3-flash count instead so
    // the visual cue is bounded and predictable.
    private const uint FLASHW_ALL = 0x00000003;

    /// <summary>Bring the supplied window to the foreground and flash its
    /// taskbar button three times.  Safe to call from the UI thread of the
    /// process that owns <paramref name="window"/>.  No-op when null.</summary>
    public static void BringToFront(Window? window)
    {
        if (window == null) return;
        try
        {
            // Step 1 — restore from minimized.
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            // Step 2 — make visible (the window may have been Hide()d into the
            // tray; this is a no-op when already visible).
            window.Show();

            // Step 3 — Topmost-toggle trick to attract foreground attention
            // without tripping Explorer's foreground-steal lockout.  The
            // sequence is the documented WPF workaround used by IDE notifiers,
            // chat clients, etc.
            var wasTopmost = window.Topmost;
            window.Topmost = true;
            window.Topmost = wasTopmost;

            window.Activate();
            window.Focus();

            // Step 4 — taskbar flash (3 blinks, default timer).  Survives the
            // foreground-steal lockout because Windows treats it as a notification
            // rather than focus theft.
            var helper = new WindowInteropHelper(window);
            var hwnd = helper.Handle;
            if (hwnd == IntPtr.Zero) return;

            var info = new FLASHWINFO
            {
                cbSize    = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd      = hwnd,
                dwFlags   = FLASHW_ALL,
                uCount    = 3,
                dwTimeout = 0,    // default cursor blink rate
            };
            FlashWindowEx(ref info);
        }
        catch (Exception ex)
        {
            // Non-fatal: pop-to-front is a UX nicety, never let it crash chat
            // receive.  LogToFile path mirrors the rest of the dispatch arms.
            IpcClient.LogToFile($"[WindowAttention] BringToFront failed: {ex.Message}");
        }
    }
}
