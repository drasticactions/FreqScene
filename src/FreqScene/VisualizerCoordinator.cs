using ProjectMDotNet;
using ProjectMDotNet.Avalonia;

namespace FreqScene;

public sealed class VisualizerCoordinator : IDisposable
{
    public const string SyntheticSourceName = "Synthetic";

    private readonly SyntheticAudioSource _synthetic;
    private readonly HashSet<ProjectMControl> _wired = [];
    private readonly List<string> _presetDirectories;
    private CaptureAudioSource? _capture;
    private ProjectMControl? _control;
    private volatile float _gain = 1.0f;
    private volatile bool _syntheticEnabled = true;
    private bool _shuffle;
    private bool _presetLocked;
    private double _presetDuration = 30;

    public VisualizerCoordinator()
    {
        _presetDirectories = Environment.GetCommandLineArgs().Skip(1).Where(Directory.Exists).ToList();

        var sources = new List<string> { SyntheticSourceName };
        sources.AddRange(OpenAlCapture.GetCaptureDevices());
        AudioSources = sources;

        _synthetic = new SyntheticAudioSource(samples =>
        {
            if (_syntheticEnabled)
            {
                ApplyGain(samples);
                _control?.AddPcm(samples, AudioChannels.Stereo);
            }
        });
        _synthetic.Start();
    }

    /// <summary>All selectable audio sources (synthetic + capture devices).</summary>
    public IReadOnlyList<string> AudioSources { get; }

    /// <summary>The currently selected audio source name.</summary>
    public string SelectedAudioSource { get; private set; } = SyntheticSourceName;

    /// <summary>Status text for UIs. May fire on any thread.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>PCM gain multiplier (0..4).</summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0f, 4f);
    }

    /// <summary>Whether the playlist plays in random order.</summary>
    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            _shuffle = value;
            if (_control?.Playlist is { } playlist)
            {
                playlist.Shuffle = value;
            }
        }
    }

    /// <summary>Whether automatic preset switching is disabled.</summary>
    public bool PresetLocked
    {
        get => _presetLocked;
        set
        {
            _presetLocked = value;
            if (_control is { } control)
            {
                control.PresetLocked = value;
            }
        }
    }

    /// <summary>Seconds each preset plays before automatic switching.</summary>
    public double PresetDuration
    {
        get => _presetDuration;
        set
        {
            _presetDuration = value;
            if (_control is { } control)
            {
                control.PresetDuration = value;
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="control"/> the active visualizer target and
    /// configures it.
    /// </summary>
    public void AttachControl(ProjectMControl control)
    {
        _control = control;
        control.PresetDuration = _presetDuration;
        control.PresetLocked = _presetLocked;

        if (_wired.Add(control))
        {
            control.InstanceCreated += (_, _) => ConfigurePlaylist(control);
            control.InitializationFailed += (_, ex) => StatusChanged?.Invoke($"initialization failed: {ex.Message}");
        }
        else if (control.Instance is not null && control.Playlist is { } playlist)
        {
            playlist.Shuffle = _shuffle;
        }
    }

    /// <summary>Clears the active target if it is still <paramref name="control"/>.</summary>
    public void DetachControl(ProjectMControl control)
    {
        if (_control == control)
        {
            _control = null;
        }
    }

    public void NextPreset() => _control?.Playlist?.PlayNext();

    public void PreviousPreset() => _control?.Playlist?.PlayPrevious();

    /// <summary>Switches the audio input; returns false if the device could not be opened.</summary>
    public bool SelectAudioSource(string name)
    {
        _capture?.Dispose();
        _capture = null;
        _syntheticEnabled = name == SyntheticSourceName;
        if (_syntheticEnabled)
        {
            SelectedAudioSource = SyntheticSourceName;
            return true;
        }

        try
        {
            _capture = new CaptureAudioSource(name, samples =>
            {
                ApplyGain(samples);
                _control?.AddPcm(samples, AudioChannels.Stereo);
            });
            _capture.Start();
            SelectedAudioSource = name;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            StatusChanged?.Invoke(ex.Message);
            _syntheticEnabled = true;
            SelectedAudioSource = SyntheticSourceName;
            return false;
        }
    }

    public void Dispose()
    {
        _control = null;
        _synthetic.Dispose();
        _capture?.Dispose();
        _capture = null;
    }

    private void ConfigurePlaylist(ProjectMControl control)
    {
        var playlist = control.EnablePlaylist();
        playlist.PresetSwitched += (_, e) =>
            StatusChanged?.Invoke($"[{e.Index}] {Path.GetFileNameWithoutExtension(playlist.GetItem(e.Index)) ?? "?"}");
        playlist.PresetSwitchFailed += (_, e) =>
            StatusChanged?.Invoke($"failed: {e.PresetFilename} — {e.Message}");
    }

    private void ApplyGain(float[] samples)
    {
        var gain = _gain;
        if (Math.Abs(gain - 1.0f) < 0.01f)
        {
            return;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = Math.Clamp(samples[i] * gain, -1.0f, 1.0f);
        }
    }

    private void ApplyGain(short[] samples)
    {
        var gain = _gain;
        if (Math.Abs(gain - 1.0f) < 0.01f)
        {
            return;
        }

        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (short)Math.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
        }
    }
}
