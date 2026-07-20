using System.Collections.ObjectModel;
using Avalonia.Threading;
using ProjectMDotNet;
using ProjectMDotNet.Avalonia;

namespace FreqScene;

public sealed class VisualizerCoordinator : IDisposable
{
    public const string SyntheticSourceName = "Synthetic";

    public static readonly string[] PresetExtensions = [".milk", ".prjm"];

    private readonly SyntheticAudioSource _synthetic;
    private readonly HashSet<ProjectMControl> _wired = [];
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private CaptureAudioSource? _capture;
    private ProjectMControl? _control;
    private volatile float _gain = 1.0f;
    private volatile bool _syntheticEnabled = true;
    private bool _shuffle;
    private bool _presetLocked;
    private double _presetDuration = 30;
    private PresetEntry? _current;
    private int _currentIndex = -1;
    private bool _loaded;
    private string _preferredAudioSource = SyntheticSourceName;

    public VisualizerCoordinator()
    {
        var state = PlaylistStore.Load();
        _preferredAudioSource = state.AudioSource ?? SyntheticSourceName;
        _gain = Math.Clamp(state.Gain, 0f, 4f);
        _shuffle = state.Shuffle;
        _presetLocked = state.PresetLocked;
        _presetDuration = state.PresetDuration;

        foreach (var entry in state.Presets.Where(File.Exists))
        {
            if (_paths.Add(entry))
            {
                Presets.Add(new PresetEntry(entry));
            }
        }

        AddPaths(Environment.GetCommandLineArgs().Skip(1));
        Renumber();

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

        if (_preferredAudioSource != SyntheticSourceName &&
            sources.Contains(_preferredAudioSource, StringComparer.Ordinal))
        {
            SelectAudioSource(_preferredAudioSource);
        }

        _loaded = true;
    }

    /// <summary>All selectable audio sources (synthetic + capture devices).</summary>
    public IReadOnlyList<string> AudioSources { get; }

    /// <summary>The currently selected audio source name.</summary>
    public string SelectedAudioSource { get; private set; } = SyntheticSourceName;

    /// <summary>Status text for UIs. May fire on any thread.</summary>
    public event Action<string>? StatusChanged;

    public ObservableCollection<PresetEntry> Presets { get; } = [];

    public event Action<int>? CurrentIndexChanged;

    public int CurrentIndex => _currentIndex;

    /// <summary>PCM gain multiplier (0..4).</summary>
    public float Gain
    {
        get => _gain;
        set
        {
            _gain = Math.Clamp(value, 0f, 4f);
            Save();
        }
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

            Save();
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

            Save();
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

            Save();
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

    public int AddPaths(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var file in Expand(paths))
        {
            if (!_paths.Add(file))
            {
                continue;
            }

            Presets.Add(new PresetEntry(file));
            _control?.Playlist?.AddPreset(file, allowDuplicates: true);
            added++;
        }

        if (added > 0)
        {
            Renumber();
            Save();

            // Adding to an idle visualizer should start playing, not wait for a switch.
            if (_current is null)
            {
                PlayAt(0);
            }
        }

        return added;
    }

    public void RemoveAt(IEnumerable<int> indices)
    {
        var removed = false;
        foreach (var index in indices.Distinct().OrderDescending())
        {
            if (index < 0 || index >= Presets.Count)
            {
                continue;
            }

            _paths.Remove(Presets[index].FullPath);
            Presets.RemoveAt(index);
            _control?.Playlist?.RemovePreset((uint)index);
            removed = true;
        }

        if (removed)
        {
            Renumber();
            Save();
        }
    }

    public void Move(int fromIndex, int toIndex)
    {
        if (fromIndex == toIndex ||
            fromIndex < 0 || fromIndex >= Presets.Count ||
            toIndex < 0 || toIndex >= Presets.Count)
        {
            return;
        }

        var entry = Presets[fromIndex];
        Presets.RemoveAt(fromIndex);
        Presets.Insert(toIndex, entry);
        if (_control?.Playlist is { } playlist)
        {
            playlist.RemovePreset((uint)fromIndex);
            playlist.InsertPreset(entry.FullPath, (uint)toIndex, allowDuplicates: true);
        }

        Renumber();
        Save();
    }

    public void SortBy(Comparison<PresetEntry> comparison)
    {
        var ordered = Presets.ToList();
        ordered.Sort(comparison);
        Presets.Clear();
        foreach (var entry in ordered)
        {
            Presets.Add(entry);
        }

        Renumber();
        RebuildNativePlaylist();
        Save();
    }

    public void ClearPresets()
    {
        Presets.Clear();
        _paths.Clear();
        _control?.Playlist?.Clear();
        SetCurrentIndex(-1);
        Save();
    }

    public void PlayAt(int index)
    {
        if (index >= 0 && index < Presets.Count)
        {
            _control?.Playlist?.SetPosition((uint)index, hardCut: true);
        }
    }

    public bool SelectAudioSource(string name)
    {
        _preferredAudioSource = name;
        _capture?.Dispose();
        _capture = null;
        _syntheticEnabled = name == SyntheticSourceName;
        if (_syntheticEnabled)
        {
            SelectedAudioSource = SyntheticSourceName;
            Save();
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
            Save();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            StatusChanged?.Invoke(ex.Message);
            _syntheticEnabled = true;
            SelectedAudioSource = SyntheticSourceName;
            Save();
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
        {
            SetCurrentIndex((int)e.Index);
            StatusChanged?.Invoke($"[{e.Index}] {Path.GetFileNameWithoutExtension(playlist.GetItem(e.Index)) ?? "?"}");
        };
        playlist.PresetSwitchFailed += (_, e) =>
            StatusChanged?.Invoke($"failed: {e.PresetFilename} — {e.Message}");

        playlist.Shuffle = _shuffle;
        RebuildNativePlaylist();
    }

    private void RebuildNativePlaylist()
    {
        if (_control?.Playlist is not { } playlist)
        {
            return;
        }

        playlist.Clear();
        foreach (var entry in Presets)
        {
            playlist.AddPreset(entry.FullPath, allowDuplicates: true);
        }

        if (Presets.Count > 0)
        {
            playlist.SetPosition((uint)Math.Max(_currentIndex, 0), hardCut: true);
        }
    }

    private static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                var files = Directory
                    .EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Where(f => PresetExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .Order(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    yield return file;
                }
            }
            else if (File.Exists(path) &&
                     PresetExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                yield return path;
            }
        }
    }

    private void Renumber()
    {
        _currentIndex = _current is null ? -1 : Presets.IndexOf(_current);
        for (var i = 0; i < Presets.Count; i++)
        {
            Presets[i].Number = i + 1;
            Presets[i].IsPlaying = ReferenceEquals(Presets[i], _current);
        }
    }

    private void SetCurrentIndex(int index)
    {
        _currentIndex = index;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyCurrentIndex(index));
            return;
        }

        ApplyCurrentIndex(index);
    }

    private void ApplyCurrentIndex(int index)
    {
        _current = index >= 0 && index < Presets.Count ? Presets[index] : null;
        for (var i = 0; i < Presets.Count; i++)
        {
            Presets[i].IsPlaying = i == index;
        }

        CurrentIndexChanged?.Invoke(index);
    }

    private void Save()
    {
        if (!_loaded)
        {
            return;
        }

        PlaylistStore.Save(new PlaylistState
        {
            Presets = Presets.Select(p => p.FullPath).ToList(),
            Shuffle = _shuffle,
            PresetLocked = _presetLocked,
            PresetDuration = _presetDuration,
            Gain = _gain,
            AudioSource = _preferredAudioSource,
        });
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
