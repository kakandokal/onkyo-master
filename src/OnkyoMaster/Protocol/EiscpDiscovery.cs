using System.Net;
using System.Net.Sockets;

namespace OnkyoMaster.Protocol;

public sealed record DiscoveredReceiver(IPAddress Address, string Model, string Id, string InterfaceId, string InterfaceName);

/// <summary>Finds receivers by broadcasting the ECN query on every active adapter.</summary>
public static class EiscpDiscovery
{
    /// <param name="interfaceId">Search only this adapter; null searches all of them.</param>
    public static async Task<List<DiscoveredReceiver>> DiscoverAsync(string? interfaceId = null, int timeoutMs = 2500, CancellationToken ct = default)
    {
        var packet = EiscpPacket.Encode("ECNQSTN", 'x');
        var probes = NetUtil.GetInterfaces()
            .Where(nic => interfaceId is null || nic.Id == interfaceId)
            .Select(nic => ProbeAsync(nic, packet, timeoutMs, ct));
        var results = await Task.WhenAll(probes);
        return results.SelectMany(r => r).GroupBy(r => r.Id).Select(g => g.First()).ToList();
    }

    private static async Task<List<DiscoveredReceiver>> ProbeAsync(LocalInterface nic, byte[] packet, int timeoutMs, CancellationToken ct)
    {
        var found = new List<DiscoveredReceiver>();
        try
        {
            // Sending to the subnet's own broadcast address lets routing pick this adapter without an explicit bind.
            using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
            var target = new IPEndPoint(nic.Broadcast, EiscpPacket.Port);
            await udp.SendAsync(packet, target, ct);
            await Task.Delay(150, ct);
            await udp.SendAsync(packet, target, ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timeoutMs);
            while (true)
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (!EiscpPacket.TryDecode(reply.Buffer, out var message, out _) || message is null) continue;
                if (!message.StartsWith("ECN", StringComparison.Ordinal)) continue;

                // "ECNTX-RZ50/60128/XX/0009B0AABBCC": model / port / region / identifier (the MAC address)
                var parts = message[3..].Split('/');
                if (parts.Length < 4) continue;
                found.Add(new DiscoveredReceiver(reply.RemoteEndPoint.Address, parts[0].Trim(), parts[3].Trim(), nic.Id, nic.Name));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        return found;
    }
}
