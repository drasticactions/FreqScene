using MeaMod.DNS.Multicast;

namespace FreqScene.Remote.Server;

public sealed class MdnsAdvertiser : IDisposable
{
    private readonly ServiceDiscovery _discovery;
    private readonly ServiceProfile _profile;

    public MdnsAdvertiser(string instanceName, int port)
    {
        _profile = new ServiceProfile(instanceName, RemoteProtocol.BonjourServiceType, (ushort)port);
        _profile.AddProperty("v", RemoteProtocol.Version.ToString());
        _profile.AddProperty("name", instanceName);
        _discovery = new ServiceDiscovery();
        _discovery.Advertise(_profile);
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
