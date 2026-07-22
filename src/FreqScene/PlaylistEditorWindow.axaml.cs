using System.Collections;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace FreqScene;

public partial class PlaylistEditorWindow : Window
{
    private static readonly DataFormat<PresetEntry> ReorderFormat =
        DataFormat.CreateInProcessFormat<PresetEntry>("freqscene-preset");

    private readonly VisualizerCoordinator _coordinator;
    private readonly PlaylistEditorViewModel _viewModel;
    private CancellationTokenSource? _importCts;
    private PointerPressedEventArgs? _dragTrigger;
    private PresetEntry? _dragCandidate;
    private Point _dragOrigin;

    public PlaylistEditorWindow(VisualizerCoordinator coordinator)
    {
        _coordinator = coordinator;
        _viewModel = new PlaylistEditorViewModel(coordinator);
        InitializeComponent();
        DataContext = _viewModel;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        PlaylistGrid.AddHandler(PointerPressedEvent, OnGridPointerPressed, RoutingStrategies.Tunnel);
        PlaylistGrid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        PlaylistGrid.PointerMoved += OnGridPointerMoved;

        coordinator.CurrentIndexChanged += OnCurrentIndexChanged;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Delete || e.Key == Key.Back)
        {
            RemoveSelected();
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && e.Key is Key.Up or Key.Down)
        {
            MoveSelected(e.Key == Key.Up ? -1 : 1);
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key == Key.L)
        {
            ScrollToCurrent();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _importCts?.Cancel();
        _coordinator.CurrentIndexChanged -= OnCurrentIndexChanged;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnCurrentIndexChanged(int index) => Dispatcher.UIThread.Post(() =>
    {
        if (IsVisible && index >= 0 && index < _coordinator.Presets.Count)
        {
            PlaylistGrid.ScrollIntoView(_coordinator.Presets[index], null);
        }
    });

    private async void OnAddFiles(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add presets",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("projectM presets") { Patterns = ["*.milk", "*.prjm"] },
            ],
        });

        await ImportItemsAsync(files);
    }

    private async void OnAddFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add preset folder",
            AllowMultiple = true,
        });

        await ImportItemsAsync(folders);
    }

    private void OnRemoveSelected(object? sender, RoutedEventArgs e) => RemoveSelected();

    private void OnClear(object? sender, RoutedEventArgs e) => _coordinator.ClearPresets();

    private async void OnAddTextureFolder(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add texture folder",
            AllowMultiple = true,
        });

        var paths = folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
        {
            _coordinator.AddTextureFolders(paths);
        }
    }

    private void OnRemoveTexture(object? sender, RoutedEventArgs e)
    {
        var indices = (TextureList.SelectedItems ?? Array.Empty<object>())
            .OfType<TextureFolderEntry>()
            .Select(entry => _coordinator.TextureFolders.IndexOf(entry))
            .ToList();
        _coordinator.RemoveTextureFolders(indices);
    }

    private void OnClearTextures(object? sender, RoutedEventArgs e) => _coordinator.ClearTextureFolders();

    private void OnSortByName(object? sender, RoutedEventArgs e) =>
        _coordinator.SortBy((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

    private void OnSortByPath(object? sender, RoutedEventArgs e) =>
        _coordinator.SortBy((a, b) => string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Selects the right-clicked row unless it is already part of the selection,
    /// so the context menu always acts on what was clicked.
    /// </summary>
    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (RowEntryAt(e.Source as Visual) is { } entry &&
            !PlaylistGrid.SelectedItems.Contains(entry))
        {
            PlaylistGrid.SelectedItem = entry;
        }
    }

    private void OnPlaySelected(object? sender, RoutedEventArgs e)
    {
        if (PlaylistGrid.SelectedItem is PresetEntry entry)
        {
            _coordinator.PlayAt(_coordinator.Presets.IndexOf(entry));
        }
    }

    private void OnGoToCurrent(object? sender, RoutedEventArgs e) => ScrollToCurrent();

    private void ScrollToCurrent()
    {
        var index = _coordinator.CurrentIndex;
        if (index < 0 || index >= _coordinator.Presets.Count)
        {
            return;
        }

        var entry = _coordinator.Presets[index];
        if (_viewModel.Items is IList items && !items.Contains(entry))
        {
            _viewModel.ClearFilter();
        }

        PlaylistGrid.SelectedItem = entry;
        PlaylistGrid.ScrollIntoView(entry, null);
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && PlaylistGrid.SelectedItem is PresetEntry entry)
        {
            _coordinator.PlayAt(_coordinator.Presets.IndexOf(entry));
            e.Handled = true;
        }
    }

    private void OnMoveToTop(object? sender, RoutedEventArgs e) => MoveSelectedToEnd(top: true);

    private void OnMoveToBottom(object? sender, RoutedEventArgs e) => MoveSelectedToEnd(top: false);

    /// <summary>Moves every selected preset to the top or bottom, keeping their relative order.</summary>
    private void MoveSelectedToEnd(bool top)
    {
        if (!_viewModel.CanReorder)
        {
            return;
        }

        var selected = PlaylistGrid.SelectedItems
            .OfType<PresetEntry>()
            .OrderBy(_coordinator.Presets.IndexOf)
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        var start = top ? 0 : _coordinator.Presets.Count - selected.Count;
        for (var i = 0; i < selected.Count; i++)
        {
            _coordinator.Move(_coordinator.Presets.IndexOf(selected[i]), start + i);
        }

        PlaylistGrid.SelectedItems.Clear();
        foreach (var entry in selected)
        {
            PlaylistGrid.SelectedItems.Add(entry);
        }

        PlaylistGrid.ScrollIntoView(selected[0], null);
    }

    private void OnMoveUp(object? sender, RoutedEventArgs e) => MoveSelected(-1);

    private void OnMoveDown(object? sender, RoutedEventArgs e) => MoveSelected(1);

    private void OnPrevious(object? sender, RoutedEventArgs e) => _coordinator.PreviousPreset();

    private void OnNext(object? sender, RoutedEventArgs e) => _coordinator.NextPreset();

    private void OnRowDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RowEntryAt(e.Source as Visual) is { } entry)
        {
            _coordinator.PlayAt(_coordinator.Presets.IndexOf(entry));
        }
    }

    private async Task ImportItemsAsync(IEnumerable<IStorageItem> items)
    {
        var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 0 || _viewModel.IsImporting)
        {
            return;
        }

        using var cts = new CancellationTokenSource();
        _importCts = cts;
        _viewModel.BeginImport();
        var progress = new Progress<ImportProgress>(_viewModel.ReportImport);
        try
        {
            await _coordinator.AddPathsAsync(paths, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user; presets added before cancellation are kept.
        }
        finally
        {
            _importCts = null;
            _viewModel.EndImport();
        }
    }

    private void OnCancelImport(object? sender, RoutedEventArgs e) => _importCts?.Cancel();

    private void RemoveSelected()
    {
        var indices = PlaylistGrid.SelectedItems
            .OfType<PresetEntry>()
            .Select(entry => _coordinator.Presets.IndexOf(entry))
            .ToList();
        _coordinator.RemoveAt(indices);
    }

    /// <summary>Moves the single selected row by <paramref name="delta"/> positions.</summary>
    private void MoveSelected(int delta)
    {
        if (!_viewModel.CanReorder || PlaylistGrid.SelectedItem is not PresetEntry entry)
        {
            return;
        }

        var from = _coordinator.Presets.IndexOf(entry);
        _coordinator.Move(from, from + delta);
        PlaylistGrid.SelectedItem = entry;
        PlaylistGrid.ScrollIntoView(entry, null);
    }

    private void OnGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragCandidate = RowEntryAt(e.Source as Visual);
            _dragTrigger = e;
            _dragOrigin = e.GetPosition(this);
        }
    }

    private async void OnGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragCandidate is not { } entry ||
            _dragTrigger is not { } trigger ||
            !_viewModel.CanReorder ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var delta = e.GetPosition(this) - _dragOrigin;
        if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4)
        {
            return;
        }

        _dragCandidate = null;
        _dragTrigger = null;
        // The item must carry a platform format as well: macOS builds one pasteboard
        // item per drag image and throws if an in-process-only format leaves it empty.
        var item = DataTransferItem.CreateText(entry.FullPath);
        item.Set(ReorderFormat, entry);
        using var data = new DataTransfer();
        data.Add(item);
        try
        {
            await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            Trace.TraceError($"Playlist reorder drag failed: {ex}");
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(ReorderFormat)
            ? DragDropEffects.Move
            : e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetValue(ReorderFormat) is { } dragged)
        {
            var target = RowEntryAt(e.Source as Visual);
            var to = target is null ? _coordinator.Presets.Count - 1 : _coordinator.Presets.IndexOf(target);
            _coordinator.Move(_coordinator.Presets.IndexOf(dragged), to);

            // Selection is positional, so it would otherwise stay on the row the
            // dragged preset used to occupy.
            PlaylistGrid.SelectedItem = dragged;
            PlaylistGrid.ScrollIntoView(dragged, null);
        }
        else if (e.DataTransfer.TryGetFiles() is { } files)
        {
            _ = ImportItemsAsync(files);
        }

        e.Handled = true;
    }

    /// <summary>The preset shown by the grid row under <paramref name="source"/>, if any.</summary>
    private static PresetEntry? RowEntryAt(Visual? source) =>
        source?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as PresetEntry;
}
