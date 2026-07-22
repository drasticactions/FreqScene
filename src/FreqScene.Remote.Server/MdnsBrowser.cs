using System.Net;
using System.Net.Sockets;
using MeaMod.DNS.Model;
using MeaMod.DNS.Multicast;

namespace FreqScene.Remote.Server;

public sealed record DiscoveredServer(string InstanceName, IPAddress Address, int Port, int ProtocolVersion)
{
    public bool IsCompatible => ProtocolVersion == RemoteProtocol.Version;

    public Uri Uri => new($"http://{Address}:{Port}");
}

public sealed class MdnsBrowser : IDisposable
{
    private static readonly TimeSpan QueryInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromSeconds(75);

    private readonly ServiceDiscovery _discovery;
    private readonly DomainName _serviceDomain;
    private readonly Timer _queryTimer;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IPAddress> _hostAddresses = new(StringComparer.OrdinalIgnoreCase);
    private volatile IReadOnlyList<DiscoveredServer> _servers = [];

    public MdnsBrowser()
    {
        _serviceDomain = new DomainName(RemoteProtocol.BonjourServiceType + ".local");
        _discovery = new ServiceDiscovery { AnswersContainsAdditionalRecords = true };
        _discovery.Mdns.AnswerReceived += OnAnswerReceived;
        _discovery.ServiceInstanceShutdown += OnInstanceShutdown;
        _queryTimer = new Timer(_ => Query(), null, TimeSpan.Zero, QueryInterval);
    }

    public IReadOnlyList<DiscoveredServer> Servers => _servers;

    public event Action? ServersChanged;

    public Uri? Resolve(string instanceName) =>
        _servers.FirstOrDefault(s =>
            string.Equals(s.InstanceName, instanceName, StringComparison.OrdinalIgnoreCase))?.Uri;

    private void Query()
    {
        try
        {
            _discovery.QueryServiceInstances(new DomainName(RemoteProtocol.BonjourServiceType));
        }
        catch (Exception)
        {
            // Network teardown races (sleep, interface changes) must not kill the timer.
        }

        Prune();
    }

    private void OnAnswerReceived(object? sender, MessageEventArgs e)
    {
        var changed = false;
        lock (_lock)
        {
            foreach (var record in e.Message.Answers.Concat(e.Message.AdditionalRecords))
            {
                changed |= Harvest(record);
            }

            if (changed)
            {
                RebuildLocked();
            }
        }

        if (changed)
        {
            ServersChanged?.Invoke();
        }
    }

    private bool Harvest(ResourceRecord record)
    {
        switch (record)
        {
            case PTRRecord ptr when ptr.Name == _serviceDomain:
                var isNew = !_entries.ContainsKey(ptr.DomainName.ToString());
                Touch(ptr.DomainName);
                return isNew;
            case SRVRecord srv when srv.Name.IsSubdomainOf(_serviceDomain):
                var entry = Touch(srv.Name);
                var host = srv.Target.ToString();
                var srvChanged = entry.Port != srv.Port || !string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase);
                entry.Port = srv.Port;
                entry.Host = host;
                return srvChanged;
            case TXTRecord txt when txt.Name.IsSubdomainOf(_serviceDomain):
                var version = txt.Strings
                    .Where(s => s.StartsWith("v=", StringComparison.Ordinal))
                    .Select(s => int.TryParse(s.AsSpan(2), out var v) ? v : (int?)null)
                    .FirstOrDefault();
                var txtEntry = Touch(txt.Name);
                var txtChanged = version is not null && txtEntry.Version != version;
                txtEntry.Version = version ?? txtEntry.Version;
                return txtChanged;
            case ARecord a:
                var key = a.Name.ToString();
                var addrChanged = !_hostAddresses.TryGetValue(key, out var existing) || !existing.Equals(a.Address);
                _hostAddresses[key] = a.Address;
                return addrChanged;
            default:
                return false;
        }
    }

    private Entry Touch(DomainName instance)
    {
        var key = instance.ToString();
        if (!_entries.TryGetValue(key, out var entry))
        {
            entry = new Entry { Label = instance.Labels.Count > 0 ? instance.Labels[0] : key };
            _entries[key] = entry;
        }

        entry.LastSeen = DateTime.UtcNow;
        return entry;
    }

    private void OnInstanceShutdown(object? sender, ServiceInstanceShutdownEventArgs e)
    {
        bool removed;
        lock (_lock)
        {
            removed = _entries.Remove(e.ServiceInstanceName.ToString());
            if (removed)
            {
                RebuildLocked();
            }
        }

        if (removed)
        {
            ServersChanged?.Invoke();
        }
    }

    private void Prune()
    {
        var changed = false;
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - EntryLifetime;
            foreach (var key in _entries.Where(kv => kv.Value.LastSeen < cutoff).Select(kv => kv.Key).ToList())
            {
                _entries.Remove(key);
                changed = true;
            }

            if (changed)
            {
                RebuildLocked();
            }
        }

        if (changed)
        {
            ServersChanged?.Invoke();
        }
    }

    private void RebuildLocked()
    {
        _servers = _entries.Values
            .Where(e => e.Host is not null && e.Port > 0)
            .Select(e => _hostAddresses.TryGetValue(e.Host!, out var address)
                ? new DiscoveredServer(e.Label, address, e.Port, e.Version ?? RemoteProtocol.Version)
                : null)
            .Where(s => s is not null && s.Address.AddressFamily == AddressFamily.InterNetwork)
            .Cast<DiscoveredServer>()
            .OrderBy(s => s.InstanceName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void Dispose()
    {
        _queryTimer.Dispose();
        _discovery.Mdns.AnswerReceived -= OnAnswerReceived;
        _discovery.ServiceInstanceShutdown -= OnInstanceShutdown;
        _discovery.Dispose();
    }

    private sealed class Entry
    {
        public required string Label { get; init; }

        public string? Host { get; set; }

        public int Port { get; set; }

        public int? Version { get; set; }

        public DateTime LastSeen { get; set; }
    }
}
