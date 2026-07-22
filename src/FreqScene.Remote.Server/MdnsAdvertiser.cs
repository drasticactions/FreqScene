using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using MeaMod.DNS.Multicast;

namespace FreqScene.Remote.Server;

public sealed class MdnsAdvertiser : IDisposable
{
    private readonly ServiceDiscovery _discovery;
    private readonly ServiceProfile _profile;

    public MdnsAdvertiser(string instanceName, int port)
    {
        // Advertise only LAN-reachable addresses; the default (every interface) includes
        // VPN tunnels and virtualization bridges that clients dial and time out on.
        var addresses = GetAdvertisableAddresses();
        _profile = addresses.Count > 0
            ? new ServiceProfile(instanceName, RemoteProtocol.BonjourServiceType, (ushort)port, addresses)
            : new ServiceProfile(instanceName, RemoteProtocol.BonjourServiceType, (ushort)port);
        _profile.AddProperty("v", RemoteProtocol.Version.ToString());
        _profile.AddProperty("name", instanceName);
        _discovery = new ServiceDiscovery();
        _discovery.Advertise(_profile);
    }

    private static List<IPAddress> GetAdvertisableAddresses()
    {
        string[] virtualPrefixes = ["utun", "awdl", "llw", "bridge", "vmnet", "anpi", "tailscale", "docker", "veth"];
        var addresses = new List<IPAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || virtualPrefixes.Any(p => nic.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork
                        && !IPAddress.IsLoopback(address)
                        && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                    {
                        addresses.Add(address);
                    }
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Fall back to the library default (all interfaces).
        }

        return addresses;
    }

    public void Dispose()
    {
        try
        {
            _discovery.Unadvertise(_profile);
        }
        catch (Exception)
        {
            // Unadvertise races with network teardown; disposal must not throw.
        }

        _discovery.Dispose();
    }
}
