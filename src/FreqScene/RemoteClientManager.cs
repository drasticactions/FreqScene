using FreqScene.Remote.Client;

namespace FreqScene;

public sealed class RemoteClientManager(VisualizerCoordinator coordinator) : IAsyncDisposable
{
    private readonly PresetCache _cache = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "FreqScene",
        "preset-cache"));

    private RemoteVisualizerSession? _session;
    private Task _applyTask = Task.CompletedTask;

    public bool IsActive => _session is not null;

    public RemoteSessionState? State => _session?.State;

    public string? HostName { get; private set; }

    public string? CurrentPresetName => _session?.CurrentPresetName;

    public event Action? StateChanged;

    public event Action<string>? StatusChanged;

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
        var session = new RemoteVisualizerSession(
            address,
            Environment.MachineName,
            $"Desktop ({Environment.OSVersion.Platform})",
            _cache,
            rediscoverAsync);
        session.PcmReceived += coordinator.MirrorPcm;
        session.PresetReceived += coordinator.MirrorPreset;
        session.StateChanged += _ => StateChanged?.Invoke();
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

    public async ValueTask DisposeAsync() =>
        await (_applyTask = TransitionAsync(_applyTask, null, null, null)).ConfigureAwait(false);
}
