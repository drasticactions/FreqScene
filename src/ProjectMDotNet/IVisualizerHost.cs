namespace ProjectMDotNet;

public interface IVisualizerHost
{
    /// <summary>The native visualizer instance; non-null after <see cref="InstanceCreated"/> until teardown.</summary>
    ProjectM? Instance { get; }

    /// <summary>The playlist created by <see cref="EnablePlaylist"/>, if any.</summary>
    ProjectMPlaylist? Playlist { get; }

    /// <summary>Seconds each preset plays before automatic switching.</summary>
    double PresetDuration { get; set; }

    /// <summary>Whether automatic preset switching is disabled.</summary>
    bool PresetLocked { get; set; }

    /// <summary>Maximum frames per second to render, or 0 for the display refresh rate.</summary>
    double MaxFrameRate { get; set; }

    /// <summary>Raised on the UI thread after the native instance has been created.</summary>
    event EventHandler? InstanceCreated;

    /// <summary>Raised when the visualizer could not be initialized.</summary>
    event EventHandler<Exception>? InitializationFailed;

    /// <summary>Queues interleaved float PCM (−1..1) for the visualizer. Callable from any thread.</summary>
    void AddPcm(ReadOnlySpan<float> interleavedSamples, AudioChannels channels);

    /// <summary>Queues interleaved 16-bit PCM for the visualizer. Callable from any thread.</summary>
    void AddPcm(ReadOnlySpan<short> interleavedSamples, AudioChannels channels);

    /// <summary>
    /// Creates (or returns) the native playlist. Only valid after
    /// <see cref="InstanceCreated"/>; call it from that event or later.
    /// </summary>
    ProjectMPlaylist EnablePlaylist();

    /// <summary>Sets the directories searched for preset textures.</summary>
    void ApplyTextureSearchPaths(IReadOnlyList<string> paths);
}
