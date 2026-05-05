using ClassroomCtrl.Shared.Discovery;
using Microsoft.Win32;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Networking.Discovery;

/// <summary>
/// Phase 9.4: Listens on UDP 7778 for the teacher's discovery beacon. Returns
/// the first matching beacon (by ChannelId) as IPEndPoint or null on timeout.
/// </summary>
public static class TeacherDiscoveryClient
{
    private const int BeaconPort = 7778;

    public static string ReadStudentChannelId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\NTY\ClassroomCtrl\Student");
            return key?.GetValue("ChannelId") as string ?? "1234";
        }
        catch { return "1234"; }
    }

    public static void WriteStudentChannelId(string id)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\NTY\ClassroomCtrl\Student");
            key?.SetValue("ChannelId", id ?? "1234");
        }
        catch { }
    }

    /// <summary>
    /// Listens for at most <paramref name="timeoutMs"/> milliseconds. Returns the first
    /// beacon whose ChannelId matches <paramref name="channelId"/>, or null on timeout.
    /// </summary>
    public static async Task<IPEndPoint?> WaitForBeaconAsync(string channelId, int timeoutMs, CancellationToken ct)
    {
        using var udp = new UdpClient(BeaconPort);
        udp.EnableBroadcast = true;
        udp.Client.ReceiveTimeout = Math.Min(timeoutMs, 5000);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    var payload = MessagePack.MessagePackSerializer.Deserialize<BeaconPayload>(result.Buffer);
                    if (string.Equals(payload.ChannelId, channelId, StringComparison.OrdinalIgnoreCase)
                        && IPAddress.TryParse(payload.TeacherIp, out var ip))
                    {
                        return new IPEndPoint(ip, payload.TcpPort);
                    }
                }
                catch { /* skip malformed beacons */ }
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) { }

        return null;
    }
}
