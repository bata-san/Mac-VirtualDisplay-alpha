// Mac-Win Bridge: Service discovery via mDNS to auto-detect Mac companion.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace MacWinBridge.Core.Discovery;

/// <summary>
/// Simple UDP broadcast-based discovery to find the Mac companion app on the network.
/// Used when config.MacHost == "auto".
/// </summary>
public sealed class BridgeDiscovery : IDisposable
{
    private const int DiscoveryPort = 42099;
    private const string DiscoveryMagic = "MACWINBRIDGE_DISCOVER";
    private const string ResponseMagic = "MACWINBRIDGE_HERE";

    private readonly ILogger<BridgeDiscovery> _logger;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;

    public BridgeDiscovery(ILogger<BridgeDiscovery> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Broadcast a discovery request and wait for response from the Mac companion.
    /// Returns the IP address of the Mac, or null if not found.
    /// </summary>
    public async Task<IPAddress?> DiscoverMacAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var udp = new UdpClient();
            udp.EnableBroadcast = true;

            var msg = System.Text.Encoding.UTF8.GetBytes(DiscoveryMagic);
            await udp.SendAsync(msg, msg.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            _logger.LogInformation("Sent discovery broadcast on port {Port}", DiscoveryPort);

            var result = await udp.ReceiveAsync(cts.Token);
            var response = System.Text.Encoding.UTF8.GetString(result.Buffer);

            if (response.StartsWith(ResponseMagic))
            {
                _logger.LogInformation("Mac discovered at {Address}", result.RemoteEndPoint.Address);
                return result.RemoteEndPoint.Address;
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
    /// Start listening for discovery requests (used in server/responder mode).
    /// </summary>
    public void StartResponder()
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ResponderLoopAsync(_cts.Token));
    }

    private async Task ResponderLoopAsync(CancellationToken ct)
    {
        _udp = new UdpClient(DiscoveryPort);
        _logger.LogInformation("Discovery responder listening on port {Port}", DiscoveryPort);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await _udp.ReceiveAsync(ct);
                var msg = System.Text.Encoding.UTF8.GetString(result.Buffer);

                if (msg == DiscoveryMagic)
                {
                    var response = System.Text.Encoding.UTF8.GetBytes(
                        $"{ResponseMagic}|{Environment.MachineName}");
                    await _udp.SendAsync(response, response.Length, result.RemoteEndPoint);
                    _logger.LogInformation("Responded to discovery from {Address}", result.RemoteEndPoint);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Get the local IP address most likely used for Mac communication.
    /// Prefers USB-C RNDIS/CDC-ECM interfaces, then falls back to general LAN.
    /// </summary>
    public static IPAddress? GetPreferredLocalAddress()
    {
        // Look for USB network adapters first (USB-C connection)
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;

            var isUsb = nic.Description.Contains("RNDIS", StringComparison.OrdinalIgnoreCase)
                     || nic.Description.Contains("CDC", StringComparison.OrdinalIgnoreCase)
                     || nic.Description.Contains("USB", StringComparison.OrdinalIgnoreCase)
                        && nic.Description.Contains("Ethernet", StringComparison.OrdinalIgnoreCase);

            if (!isUsb) continue;

            var props = nic.GetIPProperties();
            var addr = props.UnicastAddresses
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
        _cts?.Cancel();
        _cts?.Dispose();
        _udp?.Dispose();
    }
}
