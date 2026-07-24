using FreqScene.Remote.Server;

namespace FreqScene;

public sealed class RemoteServerManager : IAsyncDisposable
{
    private readonly VisualizerCoordinator coordinator;
    private readonly AppSettings settings;
    private RemoteServer? _server;
    private MdnsAdvertiser? _advertiser;
    private Task _applyTask = Task.CompletedTask;

    public RemoteServerManager(VisualizerCoordinator coordinator, AppSettings settings)
    {
        // The server serializes with MessagePackSerializer.DefaultOptions.
        Remote.Client.RemoteClientAotSupport.EnsureInitialized();
        this.coordinator = coordinator;
        this.settings = settings;
        if (string.IsNullOrEmpty(settings.ServerId))
        {
            settings.ServerId = Guid.NewGuid().ToString("N");
            SettingsStore.Save(settings);
        }

        Pairing = new PairingManager(
            settings.ServerId,
            settings.ServerDisplayName ?? Environment.MachineName,
            settings.PairedDevices);
        Pairing.DevicesChanged += PersistDevices;
    }

    public PairingManager Pairing { get; }

    /// <summary>
    /// Overrides <see cref="AppSettings.AllowRemoteConnections"/> without persisting it,
    /// so a headless run can force the server on while the desktop setting stays untouched.
    /// </summary>
    public bool? ForceEnabled { get; init; }

    /// <summary>Fires on hub threads; UI listeners must marshal themselves.</summary>
    public event Action? ClientsChanged;

    public event Action<string>? StatusChanged;

    public int ClientCount => _server?.Broadcaster.ClientCount ?? 0;

    public string? ServerName => _server?.Broadcaster.ServerName;

    /// <summary>Reconciles the running server/advertiser with the current settings.</summary>
    public Task ApplyAsync() => _applyTask = ApplyAfterAsync(_applyTask, stopAll: false);

    private async Task ApplyAfterAsync(Task previous, bool stopAll)
    {
        try
        {
            await previous.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The previous transition already reported its failure.
        }

        var wantServer = !stopAll && (ForceEnabled ?? settings.AllowRemoteConnections);

        if (wantServer && _server is null)
        {
            try
            {
                _server = await RemoteServer.StartAsync(settings.RemotePort, settings.ServerDisplayName, Pairing)
                    .ConfigureAwait(false);
                _server.Broadcaster.ClientsChanged += OnClientsChanged;
                _server.Broadcaster.NotifyPlaybackSettings(coordinator.PresetDuration, coordinator.PresetLocked);
                if (coordinator.CurrentPresetPath is { } presetPath)
                {
                    _server.Broadcaster.NotifyPresetChanged(presetPath);
                }

                coordinator.RemoteSink = _server.Broadcaster;
                StatusChanged?.Invoke($"remote server on port {settings.RemotePort}");
            }
            catch (Exception ex)
            {
                _server = null;
                StatusChanged?.Invoke($"remote server failed: {ex.Message}");
            }
        }
        else if (!wantServer && _server is { } server)
        {
            coordinator.RemoteSink = null;
            _server = null;
            server.Broadcaster.ClientsChanged -= OnClientsChanged;
            try
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"remote server stop failed: {ex.Message}");
            }

            ClientsChanged?.Invoke();
        }

        var wantBroadcast = _server is not null && settings.BroadcastServer;
        if (wantBroadcast && _advertiser is null)
        {
            try
            {
                _advertiser = new MdnsAdvertiser(_server!.Broadcaster.ServerName, settings.RemotePort, settings.ServerId);
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke($"mDNS broadcast failed: {ex.Message}");
            }
        }
        else if (!wantBroadcast && _advertiser is { } advertiser)
        {
            _advertiser = null;
            advertiser.Dispose();
        }
    }

    public void RevokeDevice(string deviceId)
    {
        _server?.Broadcaster.KickDevice(deviceId);
        Pairing.RemoveDevice(deviceId);
    }

    private void PersistDevices()
    {
        settings.PairedDevices = [.. Pairing.Devices];
        SettingsStore.Save(settings);
    }

    private void OnClientsChanged() => ClientsChanged?.Invoke();

    public async ValueTask DisposeAsync() =>
        await (_applyTask = ApplyAfterAsync(_applyTask, stopAll: true)).ConfigureAwait(false);
}
