using FreqScene.Remote.Client;

namespace FreqScene;

public sealed class RemoteClientManager(VisualizerCoordinator coordinator) : IAsyncDisposable
{
    private readonly PresetCache _cache = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "FreqScene",
        "preset-cache"));

    private readonly PairingStore _pairings = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "FreqScene",
        "pairings.json"));

    private RemoteVisualizerSession? _session;
    private Task _applyTask = Task.CompletedTask;
    private Uri? _lastAddress;
    private string? _lastHostName;
    private Func<CancellationToken, Task<Uri?>>? _lastRediscover;
    private string? _lastToken;

    public bool IsActive => _session is not null;

    public RemoteSessionState? State => _session?.State;

    public string? HostName { get; private set; }

    public string? CurrentPresetName => _session?.CurrentPresetName;

    public event Action? StateChanged;

    public event Action<string>? StatusChanged;

    /// <summary>The server wants a PIN pairing before it will stream; fires on session threads.</summary>
    public event Action? PairingRequired;

    public Task ConnectAsync(Uri address, string hostDisplayName, Func<CancellationToken, Task<Uri?>>? rediscoverAsync = null) =>
        _applyTask = TransitionAsync(_applyTask, address, hostDisplayName, rediscoverAsync);

    public Task DisconnectAsync() => _applyTask = TransitionAsync(_applyTask, null, null, null);

    private async Task TransitionAsync(
        Task previous,
        Uri? address,
        string? hostDisplayName,
        Func<CancellationToken, Task<Uri?>>? rediscoverAsync)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The previous transition already reported its failure.
        }

        if (_session is { } existing)
        {
            _session = null;
            try
            {
                await existing.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"disconnect failed: {ex.Message}");
            }
        }

        if (address is null)
        {
            HostName = null;
            coordinator.ExitMirrorMode();
            StateChanged?.Invoke();
            return;
        }

        HostName = hostDisplayName;
        _lastAddress = address;
        _lastHostName = hostDisplayName;
        _lastRediscover = rediscoverAsync;
        _lastToken = _pairings.Find(hostDisplayName, address.Host)?.Token;

        var session = new RemoteVisualizerSession(
            address,
            Environment.MachineName,
            DeviceModel,
            _cache,
            rediscoverAsync,
            authToken: _lastToken);
        session.PcmReceived += coordinator.MirrorPcm;
        session.PresetReceived += coordinator.MirrorPreset;
        session.StateChanged += state =>
        {
            if (state == RemoteSessionState.Connected)
            {
                RecordPairing(session, address);
            }
            else if (state == RemoteSessionState.PairingRequired)
            {
                PairingRequired?.Invoke();
            }

            StateChanged?.Invoke();
        };
        session.StatusChanged += message =>
        {
            StatusChanged?.Invoke(message);
            StateChanged?.Invoke();
        };

        coordinator.EnterMirrorMode();
        _session = session;
        session.Start();
        StateChanged?.Invoke();
    }

    public async Task PairAsync(string pin)
    {
        if (_lastAddress is not { } address)
        {
            throw new InvalidOperationException("No server to pair with.");
        }

        var grant = await PairingClient.PairAsync(address, pin, Environment.MachineName, DeviceModel)
            .ConfigureAwait(false);
        _pairings.Upsert(new ServerPairing
        {
            ServerId = grant.ServerId,
            ServerName = grant.ServerName,
            Host = address.Host,
            Token = grant.Token,
        });
        await ConnectAsync(address, _lastHostName ?? grant.ServerName, _lastRediscover).ConfigureAwait(false);
    }

    private void RecordPairing(RemoteVisualizerSession session, Uri address)
    {
        if (_lastToken is { } token && session.ServerId is { Length: > 0 } serverId)
        {
            _pairings.Upsert(new ServerPairing
            {
                ServerId = serverId,
                ServerName = session.ServerName ?? "",
                Host = address.Host,
                Token = token,
            });
        }
    }

    private static string DeviceModel => $"Desktop ({Environment.OSVersion.Platform})";

    public async ValueTask DisposeAsync() =>
        await (_applyTask = TransitionAsync(_applyTask, null, null, null)).ConfigureAwait(false);
}
