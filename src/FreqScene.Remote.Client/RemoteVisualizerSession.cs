using System.Text;
using Grpc.Net.Client;
using MagicOnion.Client;

namespace FreqScene.Remote.Client;

public enum RemoteSessionState
{
    Connecting,
    Connected,
    Reconnecting,
    Stopped,
}

public sealed class RemoteVisualizerSession : IAsyncDisposable
{
    private readonly string _clientName;
    private readonly string _deviceModel;
    private readonly PresetCache _cache;
    private readonly Func<CancellationToken, Task<Uri?>>? _rediscoverAsync;
    private readonly CancellationTokenSource _cts = new();
    private readonly Receiver _receiver;
    private Uri _address;
    private Task? _runTask;
    private IPresetService? _presetService;
    private int _presetVersion;
    private uint _lastSequence;
    private bool _sawSequence;
    private long _droppedChunks;

    public RemoteVisualizerSession(
        Uri address,
        string clientName,
        string deviceModel,
        PresetCache presetCache,
        Func<CancellationToken, Task<Uri?>>? rediscoverAsync = null)
    {
        RemoteClientAotSupport.EnsureInitialized();
        _address = address;
        _clientName = clientName;
        _deviceModel = deviceModel;
        _cache = presetCache;
        _rediscoverAsync = rediscoverAsync;
        _receiver = new Receiver(this);
    }

    public RemoteSessionState State { get; private set; } = RemoteSessionState.Connecting;

    public string? ServerName { get; private set; }

    public long DroppedChunks => Interlocked.Read(ref _droppedChunks);

    public event Action<RemoteSessionState>? StateChanged;

    public event Action<float[]>? PcmReceived;

    public event Action<string, bool>? PresetReceived;

    public event Action<string>? StatusChanged;

    public void Start() => _runTask ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            GrpcChannel? channel = null;
            IVisualizerHub? hub = null;
            try
            {
                SetState(attempt == 0 ? RemoteSessionState.Connecting : RemoteSessionState.Reconnecting);
                channel = GrpcChannel.ForAddress(_address, new GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler
                    {
                        ConnectTimeout = TimeSpan.FromSeconds(5),
                        KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                        KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                    },
                });
                var options = StreamingHubClientOptions.CreateWithDefault()
                    .WithClientHeartbeatInterval(TimeSpan.FromSeconds(5))
                    .WithClientHeartbeatTimeout(TimeSpan.FromSeconds(15));

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(15));
                hub = await StreamingHubClient.ConnectAsync<IVisualizerHub, IVisualizerHubReceiver>(
                    channel, _receiver, options, cancellationToken: attemptCts.Token).ConfigureAwait(false);
                _presetService = MagicOnionClient.Create<IPresetService>(channel);

                var snapshot = await hub.JoinAsync(new JoinRequest
                {
                    ClientName = _clientName,
                    DeviceModel = _deviceModel,
                }).WaitAsync(attemptCts.Token).ConfigureAwait(false);

                ServerName = snapshot.ServerName;
                _sawSequence = false;
                if (snapshot.CurrentPreset is { } preset)
                {
                    _ = ApplyPresetAsync(preset, hardCut: true);
                }

                SetState(RemoteSessionState.Connected);
                attempt = 0;

                var reason = await hub.WaitForDisconnectAsync().WaitAsync(ct).ConfigureAwait(false);
                StatusChanged?.Invoke($"disconnected: {reason.Type}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutting down; fall through to the loop exit.
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                StatusChanged?.Invoke($"connection failed: {ex.Message}");
            }
            finally
            {
                _presetService = null;
                if (hub is not null)
                {
                    try
                    {
                        await hub.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // TODO: Probably log this; the channel is likely already dead, so the dispose will fail.
                    }
                }

                channel?.Dispose();
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            SetState(RemoteSessionState.Reconnecting);
            attempt++;
            var delaySeconds = attempt switch { 1 => 1, 2 => 2, _ => 5 };
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_rediscoverAsync is not null)
            {
                try
                {
                    _address = await _rediscoverAsync(ct).ConfigureAwait(false) ?? _address;
                }
                catch (Exception)
                {
                }
            }
        }

        SetState(RemoteSessionState.Stopped);
    }

    private async Task ApplyPresetAsync(PresetInfo preset, bool hardCut)
    {
        var version = Interlocked.Increment(ref _presetVersion);
        try
        {
            var content = _cache.TryGet(preset.Id);
            if (content is null)
            {
                if (_presetService is not { } service)
                {
                    return;
                }

                var payload = await service.GetPresetAsync(preset.Id).ConfigureAwait(false);
                _cache.Store(payload.Id, payload.Content);
                content = payload.Content;
            }

            // A newer preset arrived while this one was fetching; drop the stale load.
            if (Volatile.Read(ref _presetVersion) != version)
            {
                return;
            }

            // Latin-1 keeps every byte intact; .milk files predate any encoding guarantees.
            PresetReceived?.Invoke(Encoding.Latin1.GetString(content), hardCut);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"preset '{preset.Name}' failed: {ex.Message}");
        }
    }

    private void OnPcmChunk(PcmChunk chunk)
    {
        if (_sawSequence && chunk.Sequence != _lastSequence + 1)
        {
            Interlocked.Add(ref _droppedChunks, chunk.Sequence - _lastSequence - 1);
        }

        _lastSequence = chunk.Sequence;
        _sawSequence = true;
        PcmReceived?.Invoke(chunk.Samples);
    }

    private void SetState(RemoteSessionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_runTask is { } task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _cts.Dispose();
    }

    private sealed class Receiver(RemoteVisualizerSession session) : IVisualizerHubReceiver
    {
        public void OnPresetChanged(PresetInfo preset, bool hardCut) =>
            _ = session.ApplyPresetAsync(preset, hardCut);

        public void OnPlaybackSettingsChanged(PlaybackSettings settings)
        {
            // The server drives preset switching; clients have nothing to apply yet.
        }

        public void OnPcm(PcmChunk chunk) => session.OnPcmChunk(chunk);

        public void OnServerShutdown() => session.StatusChanged?.Invoke("server shut down");
    }
}
