using MagicOnion;

namespace FreqScene.Remote;

public interface IVisualizerHub : IStreamingHub<IVisualizerHub, IVisualizerHubReceiver>
{
    Task<SessionSnapshot> JoinAsync(JoinRequest request);

    Task LeaveAsync();
}

public interface IVisualizerHubReceiver
{
    void OnPresetChanged(PresetInfo preset, bool hardCut);

    void OnPlaybackSettingsChanged(PlaybackSettings settings);

    void OnPcm(PcmChunk chunk);

    void OnServerShutdown();

    void OnRevoked();
}
