using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClassroomCtrl.Student.Service.Modules;

/// <summary>
/// Phase 6: Power-state commands the Service runs in response to teacher requests.
///
/// Why three different mechanisms?
///   • Shutdown / Restart: spawn <c>shutdown.exe</c>. It handles the SE_SHUTDOWN_NAME
///     privilege internally and works correctly when called from a service (session 0).
///   • Logoff: <c>shutdown.exe /l</c> only logs off the CALLING session. Since the
///     Service runs in session 0 (no interactive user), it can't log off the student's
///     desktop session that way. We use WTS APIs to enumerate all sessions and call
///     <c>WTSLogoffSession</c> on each Active interactive session.
/// </summary>
public static class PowerCommands
{
    public static void Shutdown(ILogger logger)
    {
        logger.LogWarning("Initiating system SHUTDOWN (force)");
        SpawnShutdown("/s /f /t 0", logger);
    }

    public static void Reboot(ILogger logger)
    {
        logger.LogWarning("Initiating system RESTART (force)");
        SpawnShutdown("/r /f /t 0", logger);
    }

    /// <summary>Logoff every Active interactive WTS session (skips session 0 = services).</summary>
    public static void LogoffActiveUsers(ILogger logger)
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var ppInfo, out uint count))
        {
            logger.LogWarning("WTSEnumerateSessions failed: {Error}", Marshal.GetLastWin32Error());
            return;
        }

        try
        {
            int size = Marshal.SizeOf<WTS_SESSION_INFO>();
            int loggedOff = 0;
            for (int i = 0; i < count; i++)
            {
                var infoPtr = IntPtr.Add(ppInfo, i * size);
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(infoPtr);
                if (info.SessionId == 0) continue;                       // services session
                if (info.State != WTS_CONNECTSTATE_CLASS.Active) continue;

                if (WTSLogoffSession(IntPtr.Zero, info.SessionId, false))
                {
                    logger.LogWarning("Logged off session {Id}", info.SessionId);
                    loggedOff++;
                }
                else
                {
                    logger.LogWarning("WTSLogoffSession({Id}) failed: {Error}",
                        info.SessionId, Marshal.GetLastWin32Error());
                }
            }
            if (loggedOff == 0)
                logger.LogInformation("No active interactive sessions found to logoff");
        }
        finally
        {
            WTSFreeMemory(ppInfo);
        }
    }

    private static void SpawnShutdown(string args, ILogger logger)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (p == null) logger.LogWarning("shutdown.exe Start returned null");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "shutdown.exe spawn failed");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    private enum WTS_CONNECTSTATE_CLASS : int
    {
        Active = 0, Connected, ConnectQuery, Shadow, Disconnected,
        Idle, Listen, Reset, Down, Init,
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, uint Reserved, uint Version,
        out IntPtr ppSessionInfo, out uint pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSLogoffSession(IntPtr hServer, uint SessionId, bool bWait);
}
