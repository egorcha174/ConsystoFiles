using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Consysto.BookPreview.Server;

/// <summary>Who counts as the home network, and how a phone there can reach this computer.</summary>
public static class LocalNetwork
{
    /// <summary>Loopback, private IPv4 ranges (10/8, 172.16/12, 192.168/16), link-local and IPv6 unique-local addresses.</summary>
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => address.GetAddressBytes() is [10, ..] or [192, 168, ..] or [169, 254, ..] || address.GetAddressBytes() is [172, var second, ..] && (second & 0xF0) == 16,
            AddressFamily.InterNetworkV6 => address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal,
            _ => false,
        };
    }

    /// <summary>
    /// Private IPv4 addresses of this computer. Adapters with a gateway come first and hide the rest:
    /// virtual switches (Hyper-V, WSL) have none, and a phone cannot reach them.
    /// </summary>
    public static IReadOnlyList<string> Addresses()
    {
        var found = new List<(string Address, bool HasGateway)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var properties = adapter.GetIPProperties();
                var hasGateway = properties.GatewayAddresses.Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any));
                foreach (var unicast in properties.UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && address.GetAddressBytes() is not [169, 254, ..] && IsLocal(address))
                        found.Add((address.ToString(), hasGateway));
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        var withGateway = found.Where(item => item.HasGateway).ToArray();
        return (withGateway.Length > 0 ? withGateway : found.ToArray()).Select(item => item.Address).Distinct().ToArray();
    }
}
