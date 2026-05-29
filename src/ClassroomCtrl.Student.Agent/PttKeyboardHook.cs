using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 13-D (Tier 3) — global low-level keyboard hook for PTT.
///
/// Why a global hook (and not WPF KeyDown / KeyUp): the Agent window is rarely
/// the focused window — the student is typically in Notepad, browser, exam app
/// while group voice chat is active.  WH_KEYBOARD_LL fires regardless of which
/// window has focus.
///
/// **Pass-through is mandatory:** the hook returns <c>CallNextHookEx</c>
/// unconditionally so the key event continues to the focused app.  Space
/// still types a space in Notepad even while it's the configured PTT key —
/// the side effect of <see cref="MicBroadcaster.IsPttDown"/> = true is ours
/// alone; we never consume the event.
///
/// Repeat-key coalesce: Windows fires WM_KEYDOWN repeatedly while the key is
/// held (auto-repeat).  Our handler is idempotent — IsPttDown = true twice
/// is the same as once.  We only act on the down→up boundary in
/// <see cref="MicBroadcaster.ApplyState"/>.
///
/// Hot-reconfigurable hotkey via <see cref="SetHotkeyVk"/> — design doc §3.5.
/// Default VK_SPACE (0x20).
/// </summary>
public sealed class PttKeyboardHook : IDisposable
{
    public const ushort DefaultHotkeyVk = 0x20; // VK_SPACE

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;

    private readonly MicBroadcaster _mic;
    private IntPtr _hookHandle = IntPtr.Zero;
    private LowLevelKeyboardProc? _proc;   // keep alive — IntPtr to delegate must not be GC'd
    private int _hotkeyVk = DefaultHotkeyVk;
    private bool _isDown;
    private bool _disposed;

    public PttKeyboardHook(MicBroadcaster mic)
    {
        _mic = mic ?? throw new ArgumentNullException(nameof(mic));
    }

    /// <summary>Install the global hook.  Should be called from the UI thread
    /// (the WH_KEYBOARD_LL hook requires a message pump; WPF Application's
    /// main thread provides one).</summary>
    public void Install()
    {
        if (_hookHandle != IntPtr.Zero || _disposed) return;
        _proc = HookCallback;
        // SetWindowsHookEx for WH_KEYBOARD_LL with null hMod (Win32 docs:
        // for global LL hooks pass the current process's module handle, but
        // it accepts NULL since Windows 10 — and NAudio + similar libraries
        // pass NULL consistently in C# global-hook examples).  Use the EXE's
        // main module handle for maximum compatibility.
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var module = process.MainModule;
        var hMod = module != null ? GetModuleHandle(module.ModuleName) : IntPtr.Zero;
        _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            IpcClient.LogToFile($"[PttKeyboardHook] SetWindowsHookEx failed: Win32 error {err}");
        }
        else
        {
            IpcClient.LogToFile($"[PttKeyboardHook] installed (default hotkey VK=0x{_hotkeyVk:X4})");
        }
    }

    public void SetHotkeyVk(ushort vk)
    {
        Interlocked.Exchange(ref _hotkeyVk, vk);
        IpcClient.LogToFile($"[PttKeyboardHook] hotkey changed to VK=0x{vk:X4}");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0 && lParam != IntPtr.Zero)
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                int vk = (int)info.vkCode;
                int target = Interlocked.CompareExchange(ref _hotkeyVk, 0, 0);
                if (vk == target)
                {
                    int msg = wParam.ToInt32();
                    bool nowDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool nowUp   = msg == WM_KEYUP   || msg == WM_SYSKEYUP;
                    if (nowDown && !_isDown)
                    {
                        _isDown = true;
                        try { _mic.IsPttDown = true; } catch { }
                    }
                    else if (nowUp && _isDown)
                    {
                        _isDown = false;
                        try { _mic.IsPttDown = false; } catch { }
                    }
                    // Down auto-repeat (key held) hits the !_isDown == false
                    // branch — coalesced.  Pass-through unchanged.
                }
            }
        }
        catch
        {
            // Swallow — a thrown exception inside a low-level hook can take
            // out the whole keyboard input stream for the system.
        }
        // CRITICAL pass-through: the focused app must still receive the key.
        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookHandle != IntPtr.Zero)
        {
            try { UnhookWindowsHookEx(_hookHandle); } catch { }
            _hookHandle = IntPtr.Zero;
        }
        // Defensive: release PTT state on hook teardown so the broadcaster
        // doesn't stay "talking" if we hot-reload the hook.
        try { _mic.IsPttDown = false; } catch { }
        _proc = null;
        IpcClient.LogToFile("[PttKeyboardHook] uninstalled");
    }

    // ─────── P/Invoke ───────

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);
}
