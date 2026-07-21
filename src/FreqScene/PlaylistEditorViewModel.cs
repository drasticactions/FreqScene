using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FreqScene;

public sealed partial class PlaylistEditorViewModel : ObservableObject, IDisposable
{
    private readonly VisualizerCoordinator _coordinator;
    private readonly ObservableCollection<PresetEntry> _matches = [];

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private IEnumerable _items;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _isImporting;

    [ObservableProperty]
    private string _importStatus = string.Empty;

    [ObservableProperty]
    private double _importValue;

    [ObservableProperty]
    private double _importMaximum;

    [ObservableProperty]
    private bool _importIndeterminate = true;

    public PlaylistEditorViewModel(VisualizerCoordinator coordinator)
    {
        _coordinator = coordinator;
        _items = coordinator.Presets;
        coordinator.Presets.CollectionChanged += OnPresetsChanged;
        UpdateStatus();
    }

    /// <summary>True when the list is unfiltered, so reordering maps 1:1 to the playlist.</summary>
    public bool CanReorder => string.IsNullOrWhiteSpace(SearchText);

    /// <summary>Texture search directories, for the Textures tab.</summary>
    public IEnumerable TextureFolders => _coordinator.TextureFolders;

    public bool Shuffle
    {
        get => _coordinator.Shuffle;
        set
        {
            if (_coordinator.Shuffle != value)
            {
                _coordinator.Shuffle = value;
                OnPropertyChanged();
            }
        }
    }

    public bool PresetLocked
    {
        get => _coordinator.PresetLocked;
        set
        {
            if (_coordinator.PresetLocked != value)
            {
                _coordinator.PresetLocked = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>PCM gain multiplier, as a double so it binds directly to a slider.</summary>
    public double Gain
    {
        get => _coordinator.Gain;
        set
        {
            if (Math.Abs(_coordinator.Gain - value) > 0.001)
            {
                _coordinator.Gain = (float)value;
                OnPropertyChanged();
            }
        }
    }

    public double PresetDuration
    {
        get => _coordinator.PresetDuration;
        set
        {
            if (Math.Abs(_coordinator.PresetDuration - value) > 0.01)
            {
                _coordinator.PresetDuration = value;
                OnPropertyChanged();
            }
        }
    }

    public void BeginImport()
    {
        IsImporting = true;
        ImportIndeterminate = true;
        ImportStatus = "Scanning…";
        ImportValue = 0;
        ImportMaximum = 0;
    }

    public void ReportImport(ImportProgress progress)
    {
        ImportIndeterminate = progress.Total <= 0;
        ImportMaximum = progress.Total;
        ImportValue = progress.Current;
        ImportStatus = progress.Total > 0
            ? $"{progress.Phase} {progress.Current:N0} / {progress.Total:N0}"
            : $"{progress.Phase} {progress.Current:N0}";
    }

    public void EndImport() => IsImporting = false;

    public void Dispose() => _coordinator.Presets.CollectionChanged -= OnPresetsChanged;

    partial void OnSearchTextChanged(string value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(CanReorder));
    }

    private void OnPresetsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!CanReorder)
        {
            ApplyFilter();
        }

        UpdateStatus();
    }

    private void ApplyFilter()
    {
        if (CanReorder)
        {
            _matches.Clear();
            Items = _coordinator.Presets;
            return;
        }

        var term = SearchText.Trim();
        _matches.Clear();
        foreach (var entry in _coordinator.Presets)
        {
            if (entry.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                entry.Directory.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                _matches.Add(entry);
            }
        }

        Items = _matches;
        UpdateStatus();
    }

    private void UpdateStatus() =>
        Status = CanReorder
            ? $"{_coordinator.Presets.Count}"
            : $"{_matches.Count}/{_coordinator.Presets.Count}";
}
