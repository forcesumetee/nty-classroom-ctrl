using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;

namespace ClassroomCtrl.Networking.Discovery;

/// <summary>
/// Minimal UDP-based discovery (NOT full mDNS — for production use a library like Zeroconf/Makaretu).
/// Teacher broadcasts "TEACHER:&lt;ip&gt;:&lt;port&gt;" every 3 sec on UDP 7778.
/// Students listen and cache the latest endpoint.
/// </summary>
public class UdpDiscoveryBeacon : IDisposable
{
    private readonly ILogger _logger;
    private readonly UdpClient _udp;
    private readonly IPEndPoint _broadcastEndpoint;
    private CancellationTokenSource? _cts;
    private readonly string _payload;

    public UdpDiscoveryBeacon(ILogger logger, IPAddress localIp, int controlPort)
    {
        _logger = logger;
        _udp = new UdpClient { EnableBroadcast = true };
        _broadcastEndpoint = new IPEndPoint(IPAddress.Broadcast, NetworkConstants.DiscoveryUdpPort);
        _payload = $"CLASSROOMCTRL:TEACHER:{localIp}:{controlPort}:v{NetworkConstants.ProtocolVersion}";
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            var bytes = Encoding.UTF8.GetBytes(_payload);
            while (!_cts.Token.IsCancellationRequested)
            {
                try { await _udp.SendAsync(bytes, _broadcastEndpoint, _cts.Token); }
                catch (Exception ex) { _logger.LogWarning(ex, "Beacon send failed"); }
                await Task.Delay(3000, _cts.Token);
            }
        }, _cts.Token);
    }

    public void Dispose() { _cts?.Cancel(); _udp.Dispose(); }
}

public class UdpDiscoveryListener : IDisposable
{
    private readonly UdpClient _udp;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cts;

    public event EventHandler<(IPAddress Ip, int Port)>? TeacherDiscovered;

    public UdpDiscoveryListener(ILogger logger)
    {
        _logger = logger;
        _udp = new UdpClient(NetworkConstants.DiscoveryUdpPort);
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udp.ReceiveAsync(_cts.Token);
                    var msg = Encoding.UTF8.GetString(result.Buffer);
                    var parts = msg.Split(':');
                    if (parts.Length >= 4 && parts[0] == "CLASSROOMCTRL" && parts[1] == "TEACHER"
                        && IPAddress.TryParse(parts[2], out var ip) && int.TryParse(parts[3], out var port))
                    {
                        TeacherDiscovered?.Invoke(this, (ip, port));
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.LogWarning(ex, "Discovery listen error"); }
            }
        }, _cts.Token);
    }

    public void Dispose() { _cts?.Cancel(); _udp.Dispose(); }
}
