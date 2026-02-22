// Mac-Win Bridge: Service discovery via UDP broadcast.
// Used when config.MacHost == "auto" to find the Mac companion.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Core.Discovery;

/// <summary>
/// UDP broadcast discovery to find the Mac companion on the local network.
/// Supports multiple broadcast attempts for reliability.
/// </summary>
public sealed class BridgeDiscovery : IDisposable
{
    private const int DiscoveryPort = 42099;
    private const string DiscoveryMagic  = "MACWINBRIDGE_DISCOVER";
    private const string ResponseMagic   = "MACWINBRIDGE_HERE";

    private readonly ILogger<BridgeDiscovery> _logger;
    private UdpClient? _responderUdp;
    private CancellationTokenSource? _responderCts;

    public BridgeDiscovery(ILogger<BridgeDiscovery> logger) => _logger = logger;

    /// <summary>
    /// Broadcast discovery requests and wait for a response from Mac.
    /// Sends multiple broadcasts for reliability.
    /// </summary>
    public async Task<IPAddress?> DiscoverMacAsync(TimeSpan timeout, int attempts = 3,
                                                    CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;

            var msg = Encoding.UTF8.GetBytes(DiscoveryMagic);

            for (int i = 0; i < attempts && !cts.Token.IsCancellationRequested; i++)
            {
                await udp.SendAsync(msg, msg.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
                _logger.LogInformation("Discovery broadcast #{N} sent on port {Port}", i + 1, DiscoveryPort);

                // Wait briefly for a response before next attempt
                var receiveTask = udp.ReceiveAsync(cts.Token);
                var delayTask = Task.Delay(TimeSpan.FromMilliseconds(timeout.TotalMilliseconds / attempts), cts.Token);
                var completed = await Task.WhenAny(receiveTask.AsTask(), delayTask);

                if (completed == receiveTask.AsTask() && receiveTask.IsCompletedSuccessfully)
                {
                    var result = await receiveTask;
                    var response = Encoding.UTF8.GetString(result.Buffer);

                    if (response.StartsWith(ResponseMagic))
                    {
                        _logger.LogInformation("Mac discovered at {Address}", result.RemoteEndPoint.Address);
                        return result.RemoteEndPoint.Address;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Discovery timed out after {Timeout}", timeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Discovery error");
        }

        return null;
    }

    /// <summary>
    /// Start listening for discovery requests (responder/server mode).
    /// </summary>
    public void StartResponder()
    {
        _responderCts = new CancellationTokenSource();
        _ = Task.Run(() => ResponderLoopAsync(_responderCts.Token));
    }

    private async Task ResponderLoopAsync(CancellationToken ct)
    {
        _responderUdp = new UdpClient(DiscoveryPort);
        _logger.LogInformation("Discovery responder listening on port {Port}", DiscoveryPort);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _responderUdp.ReceiveAsync(ct);
                var msg = Encoding.UTF8.GetString(result.Buffer);

                if (msg == DiscoveryMagic)
                {
                    var response = Encoding.UTF8.GetBytes($"{ResponseMagic}|{Environment.MachineName}");
                    await _responderUdp.SendAsync(response, response.Length, result.RemoteEndPoint);
                    _logger.LogInformation("Responded to discovery from {Address}", result.RemoteEndPoint);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Responder error");
        }
    }

    /// <summary>
    /// Get the local IP address most likely used for Mac communication.
    /// Prefers USB-C RNDIS/CDC-ECM interfaces, then falls back to general LAN.
    /// </summary>
    public static IPAddress? GetPreferredLocalAddress()
    {
        // Look for USB network adapters first (USB-C direct connection)
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var isUsb = nic.Description.Contains("RNDIS", StringComparison.OrdinalIgnoreCase)
                     || nic.Description.Contains("CDC", StringComparison.OrdinalIgnoreCase)
                     || (nic.Description.Contains("USB", StringComparison.OrdinalIgnoreCase)
                         && nic.Description.Contains("Ethernet", StringComparison.OrdinalIgnoreCase));

            if (!isUsb) continue;

            var addr = nic.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (addr is not null) return addr.Address;
        }

        // Fallback: first active non-loopback IPv4
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            ?.Address;
    }

    public void Dispose()
    {
        _responderCts?.Cancel();
        _responderCts?.Dispose();
        _responderUdp?.Dispose();
    }
}
