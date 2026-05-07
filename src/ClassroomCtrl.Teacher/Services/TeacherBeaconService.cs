using ClassroomCtrl.Shared.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 9.4: Broadcasts a small UDP beacon (port 7778) every 2 seconds so
/// student agents on the same LAN can auto-discover this teacher by ChannelId.
/// </summary>
public class TeacherBeaconService : IDisposable
{
    private const int BeaconPort = 7778;
    private const int IntervalMs = 2000;

    private readonly ILogger<TeacherBeaconService>? _logger;
    private readonly UdpClient _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public string ChannelId { get; set; } = "1234";
    public string ClassName { get; set; } = "";
    public int TcpPort { get; set; } = 7777;

    public TeacherBeaconService(ILogger<TeacherBeaconService>? logger = null)
    {
        _logger = logger;
        _udp = new UdpClient(AddressFamily.InterNetwork)
        {
            EnableBroadcast = true,
        };
        ChannelId = ReadChannelId();
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token));
        _logger?.LogInformation("TeacherBeacon started (channel={Channel})", ChannelId);
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        _loop = null;
    }

    private async Task Loop(CancellationToken ct)
    {
        var endpoint = new IPEndPoint(IPAddress.Broadcast, BeaconPort);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var payload = new BeaconPayload
                {
                    ChannelId = ChannelId,
                    TeacherIp = GetLocalIp(),
                    ClassName = ClassName,
                    TcpPort = TcpPort,
                    TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                var bytes = MessagePack.MessagePackSerializer.Serialize(payload);
                await _udp.SendAsync(bytes, bytes.Length, endpoint).ConfigureAwait(false);
            }
            catch (Exception ex) { _logger?.LogDebug(ex, "Beacon send failed"); }

            try { await Task.Delay(IntervalMs, ct).ConfigureAwait(false); } catch { break; }
        }
    }

    public void Dispose()
    {
        Stop();
        try { _udp.Dispose(); } catch { }
    }

    public static string ReadChannelId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\NTY\ClassroomCtrl");
            return key?.GetValue("ChannelId") as string ?? "1234";
        }
        catch { return "1234"; }
    }

    public static void WriteChannelId(string id)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\NTY\ClassroomCtrl");
            key?.SetValue("ChannelId", id ?? "1234");
        }
        catch { }
    }

    private static string GetLocalIp()
    {
        try
        {
            using var udp = new UdpClient();
            udp.Connect("8.8.8.8", 65530);
            return ((IPEndPoint)udp.Client.LocalEndPoint!).Address.ToString();
        }
        catch { return "0.0.0.0"; }
    }
}
