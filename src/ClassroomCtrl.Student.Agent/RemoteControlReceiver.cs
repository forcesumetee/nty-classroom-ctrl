using ClassroomCtrl.Shared.Protocol;
using System;
using System.Runtime.InteropServices;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 6.5: Receives remote-control events from the teacher and synthesizes
/// real mouse/keyboard input via SendInput. Restricted to the local console.
/// </summary>
internal static class RemoteControlReceiver
{
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
        public static int Size => Marshal.SizeOf<INPUT>();
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void HandleMouseMove(RemoteMouseMoveMessage msg)
    {
        try
        {
            int sw = GetSystemMetrics(SM_CXSCREEN);
            int sh = GetSystemMetrics(SM_CYSCREEN);
            int x = (int)(msg.NormalizedX * sw);
            int y = (int)(msg.NormalizedY * sh);
            SetCursorPos(x, y);
        }
        catch { }
    }

    public static void HandleMouseClick(RemoteMouseClickMessage msg)
    {
        try
        {
            uint flag = msg.Button switch
            {
                1 => msg.IsDown ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP,
                2 => msg.IsDown ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP,
                3 => msg.IsDown ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP,
                _ => 0,
            };
            if (flag == 0) return;
            var inp = new INPUT { type = INPUT_MOUSE, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } } };
            SendInput(1, new[] { inp }, INPUT.Size);
        }
        catch { }
    }

    public static void HandleMouseScroll(RemoteMouseScrollMessage msg)
    {
        try
        {
            var inp = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_WHEEL, mouseData = (uint)msg.Delta } }
            };
            SendInput(1, new[] { inp }, INPUT.Size);
        }
        catch { }
    }

    public static void HandleKey(RemoteKeyMessage msg)
    {
        try
        {
            var inp = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = (ushort)msg.VirtualKeyCode,
                        dwFlags = msg.IsDown ? 0u : KEYEVENTF_KEYUP,
                    }
                }
            };
            SendInput(1, new[] { inp }, INPUT.Size);
        }
        catch { }
    }
}
