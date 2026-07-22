namespace FreqScene.Remote.Server;

public interface IRemoteSink
{
    void AddPcm(ReadOnlySpan<float> interleavedSamples);

    void AddPcm(ReadOnlySpan<short> interleavedSamples);

    void NotifyPresetChanged(string presetPath);

    void NotifyPlaybackSettings(double presetDurationSeconds, bool presetLocked);
}
