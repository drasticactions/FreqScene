using System.Collections.ObjectModel;
using FreqScene.Remote.Server;
using ProjectMDotNet;

namespace FreqScene;

public sealed class VisualizerCoordinator : IDisposable
{
    public const string SyntheticSourceName = "Synthetic";

    public static readonly string[] PresetExtensions = [".milk", ".prjm"];

    private readonly SyntheticAudioSource _synthetic;
    private readonly HashSet<IVisualizerHost> _wired = [];
    private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _textureFolders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _audioLock = new();
    private CaptureAudioSource? _capture;
    private IVisualizerHost? _control;
    private volatile float _gain = 1.0f;
    private volatile bool _syntheticEnabled = true;
    private volatile bool _stopped;
    private volatile bool _audioSuspended;
    private Timer? _presetTimer;
    private Random? _shuffleRandom;
    private bool _shuffle;
    private bool _presetLocked;
    private double _presetDuration = 30;
    private int _renderScalePercent = QualityOptions.DefaultRenderScalePercent;
    private int _frameRateCap = QualityOptions.DefaultFrameRateCap;
    private volatile IRemoteSink? _remoteSink;
    private volatile bool _mirroring;
    private string? _mirrorPresetContent;
    private PresetEntry? _current;
    private int _currentIndex = -1;
    private bool _loaded;
    private string _preferredAudioSource = SyntheticSourceName;

    public VisualizerCoordinator(IEnumerable<string>? initialPaths = null)
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

        foreach (var folder in state.TextureFolders.Where(Directory.Exists))
        {
            if (_textureFolders.Add(folder))
            {
                TextureFolders.Add(new TextureFolderEntry(folder));
            }
        }

        AddPaths(initialPaths ?? Environment.GetCommandLineArgs().Skip(1));

        if (!string.IsNullOrEmpty(state.CurrentPreset))
        {
            _current = Presets.FirstOrDefault(
                p => string.Equals(p.FullPath, state.CurrentPreset, StringComparison.OrdinalIgnoreCase));
        }

        Renumber();

        var sources = new List<string> { SyntheticSourceName };
        sources.AddRange(OpenAlCapture.GetCaptureDevices());
        AudioSources = sources;

        _synthetic = new SyntheticAudioSource(samples =>
        {
            if (_syntheticEnabled && !_audioSuspended)
            {
                ApplyGain(samples);
                _control?.AddPcm(samples, AudioChannels.Stereo);
                _remoteSink?.AddPcm(samples);
                ReportLevel(samples);
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

    public IUiDispatcher UiDispatcher { get; set; } = InlineUiDispatcher.Instance;

    public Action<float>? PcmLevel { get; set; }

    /// <summary>All selectable audio sources (synthetic + capture devices).</summary>
    public IReadOnlyList<string> AudioSources { get; }

    /// <summary>The currently selected audio source name.</summary>
    public string SelectedAudioSource { get; private set; } = SyntheticSourceName;

    /// <summary>Status text for UIs. May fire on any thread.</summary>
    public event Action<string>? StatusChanged;

    public RangeObservableCollection<PresetEntry> Presets { get; } = [];

    public ObservableCollection<TextureFolderEntry> TextureFolders { get; } = [];

    public event Action<int>? CurrentIndexChanged;

    public int CurrentIndex => _currentIndex;

    public IRemoteSink? RemoteSink
    {
        get => _remoteSink;
        set
        {
            _remoteSink = value;
            UpdateAudioPipeline();
        }
    }

    public bool Stopped => _stopped;

    public bool IsMirroring => _mirroring;

    public event Action<bool>? MirroringChanged;

    public void EnterMirrorMode()
    {
        if (_mirroring)
        {
            return;
        }

        _mirroring = true;
        _presetTimer?.Dispose();
        _presetTimer = null;
        if (_control is { } control)
        {
            control.PresetLocked = true;
        }

        UpdateAudioPipeline();
        MirroringChanged?.Invoke(true);
    }

    public void ExitMirrorMode()
    {
        if (!_mirroring)
        {
            return;
        }

        _mirroring = false;
        _mirrorPresetContent = null;
        if (_control is { } control)
        {
            control.PresetLocked = _presetLocked;
            RebuildNativePlaylist();
        }

        if (_stopped)
        {
            RestartPresetTimer();
        }

        UpdateAudioPipeline();
        MirroringChanged?.Invoke(false);
    }

    public void MirrorPcm(float[] samples)
    {
        if (_mirroring)
        {
            _control?.AddPcm(samples, AudioChannels.Stereo);
        }
    }

    public void MirrorPreset(string content, bool hardCut)
    {
        if (!_mirroring)
        {
            return;
        }

        _mirrorPresetContent = content;
        _control?.Instance?.LoadPresetData(content, smoothTransition: !hardCut);
    }

    public void SetStopped(bool stopped)
    {
        if (_stopped == stopped)
        {
            return;
        }

        _stopped = stopped;
        if (stopped && !_mirroring)
        {
            RestartPresetTimer();
        }
        else
        {
            _presetTimer?.Dispose();
            _presetTimer = null;
        }

        UpdateAudioPipeline();
    }

    public string? CurrentPresetPath => _current?.FullPath;

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
            if (_control is { } control && !_mirroring)
            {
                control.PresetLocked = value;
            }

            _remoteSink?.NotifyPlaybackSettings(_presetDuration, _presetLocked);
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

            if (_stopped)
            {
                RestartPresetTimer();
            }

            _remoteSink?.NotifyPlaybackSettings(_presetDuration, _presetLocked);
            Save();
        }
    }

    public bool WallpaperTransparency { get; set; } = true;

    public int RenderScalePercent
    {
        get => _renderScalePercent;
        set
        {
            _renderScalePercent = value;
            RenderScaleChanged?.Invoke(value);
        }
    }

    public event Action<int>? RenderScaleChanged;

    public int FrameRateCap
    {
        get => _frameRateCap;
        set
        {
            _frameRateCap = value;
            if (_control is { } control)
            {
                control.MaxFrameRate = value;
            }
        }
    }

    /// <summary>
    /// Makes <paramref name="control"/> the active visualizer target and
    /// configures it.
    /// </summary>
    public void AttachControl(IVisualizerHost control)
    {
        _control = control;
        control.PresetDuration = _presetDuration;
        control.PresetLocked = _mirroring || _presetLocked;
        control.MaxFrameRate = _frameRateCap;

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
    public void DetachControl(IVisualizerHost control)
    {
        if (_control == control)
        {
            _control = null;
        }
    }

    public void NextPreset()
    {
        if (_mirroring)
        {
            return;
        }

        if (_control?.Playlist is { } playlist)
        {
            playlist.PlayNext();
        }
        else if (_stopped)
        {
            PlayDetached(NextDetachedIndex(1));
        }
    }

    public void PreviousPreset()
    {
        if (_mirroring)
        {
            return;
        }

        if (_control?.Playlist is { } playlist)
        {
            playlist.PlayPrevious();
        }
        else if (_stopped)
        {
            PlayDetached(NextDetachedIndex(-1));
        }
    }

    public int AddPaths(IEnumerable<string> paths)
    {
        var raw = paths as IReadOnlyList<string> ?? paths.ToList();

        AddTextureFolders(DetectTextureFolders(raw));

        var added = 0;
        foreach (var file in Expand(raw))
        {
            if (AddPresetFile(file))
            {
                added++;
            }
        }

        FinalizeAdd(added);
        return added;
    }

    public async Task<int> AddPathsAsync(
        IEnumerable<string> paths,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var raw = paths as IReadOnlyList<string> ?? paths.ToList();

        var existing = new HashSet<string>(_paths, StringComparer.OrdinalIgnoreCase);

        var (textureFolders, files) = await Task.Run(() =>
        {
            var textures = DetectTextureFolders(raw).ToList();
            var found = new List<string>();
            foreach (var file in Expand(raw))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existing.Add(file))
                {
                    found.Add(file);
                    if ((found.Count & 0x3FF) == 0)
                    {
                        progress?.Report(new ImportProgress("Scanning presets…", found.Count, 0));
                    }
                }
            }

            return (textures, found);
        }, cancellationToken);

        AddTextureFolders(textureFolders);

        var entries = new List<PresetEntry>(files.Count);
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (!_paths.Add(file))
                {
                    continue;
                }

                entries.Add(new PresetEntry(file));
                _control?.Playlist?.AddPreset(file, allowDuplicates: true);

                if ((i & 0x3FF) == 0x3FF)
                {
                    progress?.Report(new ImportProgress("Adding presets…", i + 1, files.Count));

                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }
        finally
        {
            Presets.AddRange(entries);
            FinalizeAdd(entries.Count);
        }

        progress?.Report(new ImportProgress("Adding presets…", files.Count, files.Count));
        return entries.Count;
    }

    private bool AddPresetFile(string file)
    {
        if (!_paths.Add(file))
        {
            return false;
        }

        Presets.Add(new PresetEntry(file));
        _control?.Playlist?.AddPreset(file, allowDuplicates: true);
        return true;
    }

    private void FinalizeAdd(int added)
    {
        if (added <= 0)
        {
            return;
        }

        Renumber();
        Save();

        // Adding to an idle visualizer should start playing, not wait for a switch.
        if (_current is null)
        {
            PlayAt(0);
        }
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

    public int AddTextureFolders(IEnumerable<string> folders)
    {
        var added = 0;
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder) || !_textureFolders.Add(folder))
            {
                continue;
            }

            TextureFolders.Add(new TextureFolderEntry(folder));
            added++;
        }

        if (added > 0)
        {
            ApplyTextureSearchPaths(reloadCurrent: true);
            Save();
        }

        return added;
    }

    public void RemoveTextureFolders(IEnumerable<int> indices)
    {
        var removed = false;
        foreach (var index in indices.Distinct().OrderDescending())
        {
            if (index < 0 || index >= TextureFolders.Count)
            {
                continue;
            }

            _textureFolders.Remove(TextureFolders[index].FullPath);
            TextureFolders.RemoveAt(index);
            removed = true;
        }

        if (removed)
        {
            ApplyTextureSearchPaths(reloadCurrent: true);
            Save();
        }
    }

    public void ClearTextureFolders()
    {
        if (TextureFolders.Count == 0)
        {
            return;
        }

        TextureFolders.Clear();
        _textureFolders.Clear();
        ApplyTextureSearchPaths(reloadCurrent: true);
        Save();
    }

    public void PlayAt(int index)
    {
        if (_mirroring || index < 0 || index >= Presets.Count)
        {
            return;
        }

        if (_control?.Playlist is { } playlist)
        {
            playlist.SetPosition((uint)index, hardCut: true);
        }
        else if (_stopped)
        {
            PlayDetached(index);
        }
    }

    private void RestartPresetTimer()
    {
        var period = TimeSpan.FromSeconds(Math.Max(_presetDuration, 1));
        if (_presetTimer is { } timer)
        {
            timer.Change(period, period);
        }
        else
        {
            _presetTimer = new Timer(_ => UiDispatcher.Post(AdvanceDetached), null, period, period);
        }
    }

    private void AdvanceDetached()
    {
        if (!_stopped || _presetLocked || _mirroring)
        {
            return;
        }

        PlayDetached(NextDetachedIndex(1));
    }

    /// <summary>Switches presets without a native playlist (host stopped): remote-only.</summary>
    private void PlayDetached(int index)
    {
        if (index < 0 || index >= Presets.Count)
        {
            return;
        }

        SetCurrentIndex(index);
        var path = Presets[index].FullPath;
        _remoteSink?.NotifyPresetChanged(path);
        StatusChanged?.Invoke($"[{index}] {Path.GetFileNameWithoutExtension(path)}");

        if (_stopped)
        {
            RestartPresetTimer();
        }
    }

    private int NextDetachedIndex(int step)
    {
        var count = Presets.Count;
        if (count == 0)
        {
            return -1;
        }

        if (_shuffle && count > 1)
        {
            _shuffleRandom ??= new Random();
            int next;
            do
            {
                next = _shuffleRandom.Next(count);
            }
            while (next == _currentIndex);
            return next;
        }

        var current = Math.Max(_currentIndex, 0);
        return (((current + step) % count) + count) % count;
    }

    public bool SelectAudioSource(string name)
    {
        lock (_audioLock)
        {
            _preferredAudioSource = name;
            _capture?.Dispose();
            _capture = null;
            _syntheticEnabled = name == SyntheticSourceName;

            if (_syntheticEnabled || _audioSuspended || TryStartCapture(name))
            {
                SelectedAudioSource = name;
                Save();
                return true;
            }

            _syntheticEnabled = true;
            SelectedAudioSource = SyntheticSourceName;
            Save();
            return false;
        }
    }

    private void UpdateAudioPipeline()
    {
        lock (_audioLock)
        {
            var suspend = _mirroring || (_stopped && _remoteSink is null);
            if (suspend == _audioSuspended)
            {
                return;
            }

            _audioSuspended = suspend;
            if (suspend)
            {
                _capture?.Dispose();
                _capture = null;
            }
            else if (!_syntheticEnabled && _capture is null && !TryStartCapture(_preferredAudioSource))
            {
                _syntheticEnabled = true;
                SelectedAudioSource = SyntheticSourceName;
            }
        }
    }

    private bool TryStartCapture(string name)
    {
        try
        {
            _capture = new CaptureAudioSource(name, samples =>
            {
                if (_audioSuspended)
                {
                    return;
                }

                ApplyGain(samples);
                _control?.AddPcm(samples, AudioChannels.Stereo);
                _remoteSink?.AddPcm(samples);
                ReportLevel(samples);
            });
            _capture.Start();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            _capture = null;
            StatusChanged?.Invoke(ex.Message);
            return false;
        }
    }

    public void Dispose()
    {
        Save();
        _control = null;
        _presetTimer?.Dispose();
        _presetTimer = null;
        _synthetic.Dispose();
        _capture?.Dispose();
        _capture = null;
    }

    private void ConfigurePlaylist(IVisualizerHost control)
    {
        var playlist = control.EnablePlaylist();
        playlist.PresetSwitched += (_, e) =>
        {
            SetCurrentIndex((int)e.Index);
            if (playlist.GetItem(e.Index) is { } switchedPath)
            {
                _remoteSink?.NotifyPresetChanged(switchedPath);
            }

            StatusChanged?.Invoke($"[{e.Index}] {Path.GetFileNameWithoutExtension(playlist.GetItem(e.Index)) ?? "?"}");
        };
        playlist.PresetSwitchFailed += (_, e) =>
            StatusChanged?.Invoke($"failed: {e.PresetFilename} — {e.Message}");

        playlist.Shuffle = _shuffle;

        ApplyTextureSearchPaths(reloadCurrent: false);

        if (_mirroring)
        {
            control.PresetLocked = true;
            if (_mirrorPresetContent is { } content)
            {
                control.Instance?.LoadPresetData(content, smoothTransition: false);
            }

            return;
        }

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

    private static IEnumerable<string> DetectTextureFolders(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var container in new[] { path, Path.GetDirectoryName(path) })
            {
                if (string.IsNullOrEmpty(container) || !Directory.Exists(container))
                {
                    continue;
                }

                foreach (var sub in Directory.EnumerateDirectories(container))
                {
                    if (string.Equals(Path.GetFileName(sub), "textures", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return sub;
                    }
                }
            }
        }
    }

    private List<string> ExpandTexturePaths()
    {
        var expanded = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in _textureFolders)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            if (seen.Add(root))
            {
                expanded.Add(root);
            }

            try
            {
                foreach (var sub in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                {
                    if (seen.Add(sub))
                    {
                        expanded.Add(sub);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A folder that becomes unreadable mid-enumeration just contributes fewer paths.
            }
        }

        return expanded;
    }

    private void ApplyTextureSearchPaths(bool reloadCurrent)
    {
        if (_control is not { } control)
        {
            return;
        }

        control.ApplyTextureSearchPaths(ExpandTexturePaths());

        if (reloadCurrent && control.Playlist is { Count: > 0 } playlist)
        {
            playlist.SetPosition(playlist.Position, hardCut: true);
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
        if (!UiDispatcher.CheckAccess())
        {
            UiDispatcher.Post(() => ApplyCurrentIndex(index));
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
        Save();
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
            TextureFolders = TextureFolders.Select(t => t.FullPath).ToList(),
            Shuffle = _shuffle,
            PresetLocked = _presetLocked,
            PresetDuration = _presetDuration,
            Gain = _gain,
            AudioSource = _preferredAudioSource,
            CurrentPreset = _current?.FullPath,
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

    private void ReportLevel(float[] samples)
    {
        if (PcmLevel is not { } sink)
        {
            return;
        }

        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        sink(Math.Min(peak, 1f));
    }

    private void ReportLevel(short[] samples)
    {
        if (PcmLevel is not { } sink)
        {
            return;
        }

        var peak = 0;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs((int)sample));
        }

        sink(Math.Min(peak / 32768f, 1f));
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
