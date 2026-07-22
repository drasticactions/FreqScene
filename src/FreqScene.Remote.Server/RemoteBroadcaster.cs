using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Cysharp.Runtime.Multicast;

namespace FreqScene.Remote.Server;

public sealed record RemoteClientInfo(Guid ConnectionId, string Name, string DeviceModel);

public sealed class RemoteBroadcaster : IRemoteSink, IDisposable
{
    private readonly IMulticastSyncGroup<Guid, IVisualizerHubReceiver> _group;
    private readonly ConcurrentDictionary<Guid, RemoteClientInfo> _clients = new();
    private readonly ConcurrentDictionary<string, (DateTime LastWrite, PresetInfo Info)> _pathCache = new();
    private readonly ConcurrentDictionary<string, string> _idToPath = new();
    private readonly Lock _gate = new();
    private readonly float[] _pending = new float[RemoteProtocol.PcmChunkSamples];
    private int _pendingCount;
    private uint _sequence;
    private volatile int _clientCount;
    private PresetInfo? _currentPreset;
    private double _presetDuration = 30;
    private bool _presetLocked;

    public RemoteBroadcaster(IMulticastGroupProvider groupProvider)
    {
        _group = groupProvider.GetOrAddSynchronousGroup<Guid, IVisualizerHubReceiver>("visualizers");
    }

    public string ServerName { get; set; } = Environment.MachineName;

    public int ClientCount => _clientCount;

    public IReadOnlyCollection<RemoteClientInfo> Clients => _clients.Values.ToArray();

    /// <summary>Fires on hub threads; UI listeners must marshal themselves.</summary>
    public event Action? ClientsChanged;

    internal SessionSnapshot Register(Guid connectionId, IVisualizerHubReceiver receiver, JoinRequest request)
    {
        _group.Add(connectionId, receiver);
        _clients[connectionId] = new RemoteClientInfo(connectionId, request.ClientName, request.DeviceModel);
        _clientCount = _clients.Count;
        ClientsChanged?.Invoke();

        lock (_gate)
        {
            return new SessionSnapshot
            {
                ServerName = ServerName,
                CurrentPreset = _currentPreset,
                PresetDurationSeconds = _presetDuration,
                PresetLocked = _presetLocked,
            };
        }
    }

    internal void Unregister(Guid connectionId)
    {
        _group.Remove(connectionId);
        if (_clients.TryRemove(connectionId, out _))
        {
            _clientCount = _clients.Count;
            ClientsChanged?.Invoke();
        }
    }

    internal PresetPayload? GetPresetPayload(string presetId)
    {
        if (!_idToPath.TryGetValue(presetId, out var path))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllBytes(path);
            return new PresetPayload
            {
                Id = presetId,
                Name = Path.GetFileNameWithoutExtension(path),
                Content = content,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void AddPcm(ReadOnlySpan<float> interleavedSamples)
    {
        if (_clientCount == 0)
        {
            lock (_gate)
            {
                _pendingCount = 0;
            }

            return;
        }

        lock (_gate)
        {
            while (!interleavedSamples.IsEmpty)
            {
                var take = Math.Min(interleavedSamples.Length, _pending.Length - _pendingCount);
                interleavedSamples[..take].CopyTo(_pending.AsSpan(_pendingCount));
                _pendingCount += take;
                interleavedSamples = interleavedSamples[take..];

                if (_pendingCount == _pending.Length)
                {
                    var chunk = new PcmChunk { Sequence = _sequence++, Samples = _pending.ToArray() };
                    _pendingCount = 0;
                    _group.All.OnPcm(chunk);
                }
            }
        }
    }

    public void AddPcm(ReadOnlySpan<short> interleavedSamples)
    {
        if (_clientCount == 0)
        {
            return;
        }

        var rented = ArrayPool<float>.Shared.Rent(interleavedSamples.Length);
        try
        {
            for (var i = 0; i < interleavedSamples.Length; i++)
            {
                rented[i] = interleavedSamples[i] / 32768f;
            }

            AddPcm(rented.AsSpan(0, interleavedSamples.Length));
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }

    public void NotifyPresetChanged(string presetPath)
    {
        PresetInfo info;
        try
        {
            info = DescribePreset(presetPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        lock (_gate)
        {
            _currentPreset = info;
        }

        // Desktop switches (auto or manual) always blend on clients; hard cuts are reserved
        // for reload-in-place scenarios.
        _group.All.OnPresetChanged(info, hardCut: false);
    }

    public void NotifyPlaybackSettings(double presetDurationSeconds, bool presetLocked)
    {
        lock (_gate)
        {
            _presetDuration = presetDurationSeconds;
            _presetLocked = presetLocked;
        }

        _group.All.OnPlaybackSettingsChanged(new PlaybackSettings
        {
            PresetDurationSeconds = presetDurationSeconds,
            PresetLocked = presetLocked,
        });
    }

    public void BroadcastShutdown() => _group.All.OnServerShutdown();

    private PresetInfo DescribePreset(string path)
    {
        var lastWrite = File.GetLastWriteTimeUtc(path);
        if (_pathCache.TryGetValue(path, out var cached) && cached.LastWrite == lastWrite)
        {
            return cached.Info;
        }

        var content = File.ReadAllBytes(path);
        var info = new PresetInfo
        {
            Id = Convert.ToHexStringLower(SHA256.HashData(content)),
            Name = Path.GetFileNameWithoutExtension(path),
            ByteSize = content.Length,
        };
        _pathCache[path] = (lastWrite, info);
        _idToPath[info.Id] = path;
        return info;
    }

    public void Dispose() => _group.Dispose();
}
