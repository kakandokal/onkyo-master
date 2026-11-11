using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OnkyoMaster.Protocol;

/// <summary>One IPv4 address of an active network adapter.</summary>
public sealed record LocalInterface(string Id, string Name, IPAddress Address, IPAddress Mask)
{
    public IPAddress Broadcast
    {
        get
        {
            var ip = Address.GetAddressBytes();
            var mask = Mask.GetAddressBytes();
            for (var i = 0; i < 4; i++) ip[i] |= (byte)~mask[i];
            return new IPAddress(ip);
        }
    }

    /// <summary>True when <paramref name="remote"/> is in this adapter's subnet.</summary>
    public bool Contains(IPAddress remote)
    {
        if (remote.AddressFamily != AddressFamily.InterNetwork) return false;
        var a = Address.GetAddressBytes();
        var b = remote.GetAddressBytes();
        var m = Mask.GetAddressBytes();
        for (var i = 0; i < 4; i++)
        {
            if ((a[i] & m[i]) != (b[i] & m[i])) return false;
        }
        return true;
    }
}

/// <summary>
/// What the binding setting means for one connection: bind to <see cref="Local"/>, connect unbound
/// (<see cref="Local"/> is null), or — when <see cref="Available"/> is false — don't connect at all
/// because the adapter the setting asks for is not there.
/// </summary>
public readonly record struct BindChoice(bool Available, IPAddress? Local)
{
    public static readonly BindChoice Unbound = new(true, null);
    public static readonly BindChoice Unavailable = new(false, null);
}

public static class NetUtil
{
    /// <summary>Binding modes stored in settings; any other value is an adapter id.</summary>
    public const string BindNone = "";
    public const string BindAuto = "auto";

    public static List<LocalInterface> GetInterfaces()
    {
        var result = new List<LocalInterface>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = unicast.Address.GetAddressBytes();
                    if (bytes[0] == 169 && bytes[1] == 254) continue; // no DHCP lease, adapter is not really connected
                    result.Add(new LocalInterface(nic.Id, nic.Name, unicast.Address, unicast.IPv4Mask));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return result;
    }

    /// <summary>
    /// Applies the binding setting to a receiver address. Binding is what gets traffic past a VPN route
    /// that covers the receiver's address. A binding mode never falls back to an unbound connection:
    /// that would send the traffic wherever that route leads, to some unrelated host.
    /// </summary>
    public static BindChoice ResolveBind(string? mode, IPAddress remote)
    {
        if (string.IsNullOrEmpty(mode)) return BindChoice.Unbound;
        var candidates = GetInterfaces().Where(i => mode == BindAuto ? i.Contains(remote) : i.Id == mode).ToList();
        var pick = candidates.FirstOrDefault(i => i.Contains(remote)) ?? candidates.FirstOrDefault();
        return pick is null ? BindChoice.Unavailable : new BindChoice(true, pick.Address);
    }
}
