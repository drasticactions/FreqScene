using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using FreqScene.Remote.Server;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace FreqScene.Tui;

public sealed class MainView : Runnable
{
    private const int LevelMeterWidth = 16;
    private const int MaxLogLines = 300;

    private readonly IApplication _app;
    private readonly VisualizerCoordinator _coordinator;
    private readonly RemoteServerManager _remoteManager;
    private readonly AppSettings _settings;

    private readonly ConcurrentQueue<string> _pendingLog = new();
    private readonly ObservableCollection<string> _log = [];
    private readonly FrameView _presetsFrame;
    private readonly ListView _presetList;
    private readonly ListView _logView;
    private readonly Label _playingLabel;
    private readonly Label _modeLabel;
    private readonly Label _audioLabel;
    private readonly Label _levelLabel;
    private readonly Label _remoteLabel;
    private readonly Label _clientsLabel;
    private readonly Label _mdnsLabel;

    private float _peakLevel;
    private float _shownLevel;
    private object? _timeout;

    public MainView(
        IApplication app,
        VisualizerCoordinator coordinator,
        RemoteServerManager remoteManager,
        AppSettings settings)
    {
        _app = app;
        _coordinator = coordinator;
        _remoteManager = remoteManager;
        _settings = settings;

        Title = "FreqScene";

        MenuBar menuBar = BuildMenuBar();

        _presetsFrame = new FrameView
        {
            Title = "Presets",
            X = 0,
            Y = Pos.Bottom(menuBar),
            Width = Dim.Percent(60),
            Height = Dim.Fill(8),
        };
        _presetList = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        _presetList.SetSource(coordinator.Presets);
        _presetList.Accepted += (_, _) => PlaySelected();
        _presetsFrame.Add(_presetList);

        FrameView statusFrame = new()
        {
            Title = "Status",
            X = Pos.Right(_presetsFrame),
            Y = Pos.Bottom(menuBar),
            Width = Dim.Fill(),
            Height = Dim.Fill(8),
        };
        _playingLabel = new Label { X = 1, Y = 0, Width = Dim.Fill(1) };
        _modeLabel = new Label { X = 1, Y = 1, Width = Dim.Fill(1) };
        _audioLabel = new Label { X = 1, Y = 2, Width = Dim.Fill(1) };
        _levelLabel = new Label { X = 1, Y = 3, Width = Dim.Fill(1) };
        _remoteLabel = new Label { X = 1, Y = 5, Width = Dim.Fill(1) };
        _clientsLabel = new Label { X = 1, Y = 6, Width = Dim.Fill(1) };
        _mdnsLabel = new Label { X = 1, Y = 7, Width = Dim.Fill(1) };
        statusFrame.Add(
            _playingLabel, _modeLabel, _audioLabel, _levelLabel,
            _remoteLabel, _clientsLabel, _mdnsLabel);

        FrameView logFrame = new()
        {
            Title = "Log",
            X = 0,
            Y = Pos.AnchorEnd(8),
            Width = Dim.Fill(),
            Height = 7,
        };
        _logView = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        _logView.SetSource(_log);
        logFrame.Add(_logView);

        Add(menuBar, _presetsFrame, statusFrame, logFrame, BuildStatusBar());

        coordinator.StatusChanged += message => _pendingLog.Enqueue(message);
        remoteManager.StatusChanged += message => _pendingLog.Enqueue($"[remote] {message}");
        remoteManager.ClientsChanged += () => _pendingLog.Enqueue("[remote] client list changed");
        coordinator.PcmLevel = level =>
        {
            if (level > _peakLevel)
            {
                _peakLevel = level;
            }
        };

        UpdateStatus();
    }

    protected override void OnIsRunningChanged(bool newIsRunning)
    {
        base.OnIsRunningChanged(newIsRunning);

        if (newIsRunning)
        {
            _timeout = App!.AddTimeout(TimeSpan.FromMilliseconds(100), OnTick);
        }
        else if (_timeout is { } timeout)
        {
            App?.RemoveTimeout(timeout);
            _timeout = null;
        }
    }

    private bool OnTick()
    {
        while (_pendingLog.TryDequeue(out var message))
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {message}");
            while (_log.Count > MaxLogLines)
            {
                _log.RemoveAt(0);
            }

            _logView.MoveEnd(false);
        }

        var peak = _peakLevel;
        _peakLevel = 0f;
        _shownLevel = Math.Max(peak, _shownLevel * 0.75f);

        UpdateStatus();
        return true;
    }

    private void UpdateStatus()
    {
        _presetsFrame.Title = $"Presets [{_coordinator.Presets.Count}]";

        var index = _coordinator.CurrentIndex;
        _playingLabel.Text = index >= 0 && index < _coordinator.Presets.Count
            ? $"▶ {_coordinator.Presets[index].Name}"
            : "▶ (nothing playing)";

        _modeLabel.Text =
            $"Lock: {(_coordinator.PresetLocked ? "on" : "off")}   " +
            $"Shuffle: {(_coordinator.Shuffle ? "on" : "off")}   " +
            $"Duration: {_coordinator.PresetDuration:0}s";
        _audioLabel.Text = $"Audio: {_coordinator.SelectedAudioSource}   Gain: {_coordinator.Gain:0.0}";

        var filled = (int)Math.Round(_shownLevel * LevelMeterWidth);
        _levelLabel.Text = $"Level: {new string('█', filled)}{new string('░', LevelMeterWidth - filled)}";

        _remoteLabel.Text = _settings.AllowRemoteConnections
            ? $"Remote: ● port {_settings.RemotePort}"
            : "Remote: off";
        var count = _remoteManager.ClientCount;
        _clientsLabel.Text = $"Clients: {count}";
        _mdnsLabel.Text = _settings is { AllowRemoteConnections: true, BroadcastServer: true }
            ? "mDNS: broadcasting"
            : "mDNS: off";

        _presetList.SetNeedsDraw();
    }

    private MenuBar BuildMenuBar()
    {
        MenuBar menuBar = new();

        menuBar.Add(new MenuBarItem("_File",
        [
            new MenuItem { Title = "_Add Presets…", Action = AddPresets },
            new MenuItem { Title = "_Clear Playlist", Action = ClearPlaylist },
            new Line(),
            new MenuItem { Title = "_Quit", Action = () => App?.RequestStop() },
        ]));

        menuBar.Add(new MenuBarItem("_Playlist",
        [
            new MenuItem { Title = "_Next", Action = _coordinator.NextPreset },
            new MenuItem { Title = "_Previous", Action = _coordinator.PreviousPreset },
            new MenuItem { Title = "Play _Selected", Action = PlaySelected },
            new Line(),
            new MenuItem { Title = "Toggle _Lock", Action = ToggleLock },
            new MenuItem { Title = "Toggle Sh_uffle", Action = ToggleShuffle },
            new Line(),
            new MenuItem { Title = "_Remove Selected", Action = RemoveSelected },
            new MenuItem { Title = "Sort by _Name", Action = () => SortPresets(byName: true) },
            new MenuItem { Title = "Sort by Pa_th", Action = () => SortPresets(byName: false) },
        ]));

        menuBar.Add(new MenuBarItem("_Audio",
        [
            new MenuItem { Title = "_Select Source…", Action = SelectAudioSource },
            new MenuItem { Title = "_Gain…", Action = PromptGain },
        ]));

        menuBar.Add(new MenuBarItem("_Remote",
        [
            new MenuItem { Title = "Toggle _Remote Connections", Action = ToggleRemote },
            new MenuItem { Title = "Toggle _Broadcast (mDNS)", Action = ToggleBroadcast },
            new Line(),
            new MenuItem { Title = "Pair a De_vice…", Action = ShowPairing },
            new MenuItem { Title = "Paired D_evices…", Action = ShowPairedDevices },
            new Line(),
            new MenuItem { Title = "Preset _Duration…", Action = PromptDuration },
        ]));

        menuBar.Add(new MenuBarItem("_Help",
        [
            new MenuItem { Title = "_Keys…", Action = ShowKeys },
        ]));

        return menuBar;
    }

    private StatusBar BuildStatusBar()
    {
        StatusBar statusBar = new();

        statusBar.Add(
            MakeShortcut(Key.F5, "Prev", _coordinator.PreviousPreset),
            MakeShortcut(Key.F6, "Next", _coordinator.NextPreset),
            MakeShortcut(Key.F7, "Lock", ToggleLock),
            MakeShortcut(Key.F8, "Shuffle", ToggleShuffle),
            MakeShortcut(Key.Q.WithCtrl, "Quit", () => App?.RequestStop()));

        return statusBar;
    }

    private static Shortcut MakeShortcut(Key key, string title, Action action)
    {
        Shortcut shortcut = new()
        {
            Title = title,
            Key = key,
            BindKeyToApplication = true,
        };
        shortcut.Activated += (_, _) => action();
        return shortcut;
    }

    private void PlaySelected()
    {
        if (_presetList.Value is { } index)
        {
            _coordinator.PlayAt(index);
        }
    }

    private void RemoveSelected()
    {
        if (_presetList.Value is { } index)
        {
            _coordinator.RemoveAt([index]);
        }
    }

    private void SortPresets(bool byName)
    {
        _coordinator.SortBy((a, b) => byName
            ? string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase)
            : string.Compare(a.FullPath, b.FullPath, StringComparison.OrdinalIgnoreCase));
        _pendingLog.Enqueue(byName ? "sorted by name" : "sorted by path");
    }

    private void ToggleLock()
    {
        _coordinator.PresetLocked = !_coordinator.PresetLocked;
        _pendingLog.Enqueue(_coordinator.PresetLocked ? "preset locked" : "preset unlocked");
    }

    private void ToggleShuffle()
    {
        _coordinator.Shuffle = !_coordinator.Shuffle;
        _pendingLog.Enqueue(_coordinator.Shuffle ? "shuffle on" : "shuffle off");
    }

    private void AddPresets()
    {
        using OpenDialog dialog = new()
        {
            Title = "Add Presets (files or directories)",
            OpenMode = OpenMode.Mixed,
            AllowsMultipleSelection = true,
        };
        App!.Run(dialog);

        if (dialog.Canceled || dialog.FilePaths.Count == 0)
        {
            return;
        }

        var added = _coordinator.AddPaths(dialog.FilePaths);
        _pendingLog.Enqueue($"added {added} preset(s)");
    }

    private void ClearPlaylist()
    {
        if (MessageBox.Query(App!, "Clear Playlist", "Remove all presets from the playlist?", "Cancel", "Clear") == 1)
        {
            _coordinator.ClearPresets();
            _pendingLog.Enqueue("playlist cleared");
        }
    }

    private void SelectAudioSource()
    {
        using Dialog dialog = new() { Title = "Audio Source" };
        ObservableCollection<string> sources = [.. _coordinator.AudioSources];
        ListView list = new() { Width = 40, Height = Math.Min(sources.Count, 10) };
        list.SetSource(sources);
        list.Value = _coordinator.AudioSources.ToList().IndexOf(_coordinator.SelectedAudioSource);
        dialog.Add(list);
        dialog.AddButton(new Button { Title = "_Cancel" });
        dialog.AddButton(new Button { Title = "_Select" });
        App!.Run(dialog);

        if (dialog.Canceled || list.Value is not { } index)
        {
            return;
        }

        var name = sources[index];
        _pendingLog.Enqueue(_coordinator.SelectAudioSource(name)
            ? $"audio source: {name}"
            : $"audio source {name} unavailable, using {_coordinator.SelectedAudioSource}");
    }

    private void PromptGain()
    {
        if (PromptValue("Gain", "PCM gain (0.0 – 4.0):", _coordinator.Gain.ToString("0.0", CultureInfo.InvariantCulture)) is { } text &&
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var gain))
        {
            _coordinator.Gain = gain;
            _pendingLog.Enqueue($"gain: {_coordinator.Gain:0.0}");
        }
    }

    private void PromptDuration()
    {
        if (PromptValue("Preset Duration", "Seconds per preset:", _coordinator.PresetDuration.ToString("0", CultureInfo.InvariantCulture)) is { } text &&
            double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
            seconds >= 1)
        {
            _coordinator.PresetDuration = seconds;
            _pendingLog.Enqueue($"preset duration: {seconds:0}s");
        }
    }

    private string? PromptValue(string title, string prompt, string current)
    {
        using Dialog dialog = new() { Title = title };
        Label label = new() { Text = prompt, X = 0, Y = 0 };
        TextField field = new() { X = 0, Y = 1, Width = 30, Text = current };
        dialog.Add(label, field);
        dialog.AddButton(new Button { Title = "_Cancel" });
        dialog.AddButton(new Button { Title = "_Ok" });
        App!.Run(dialog);

        return dialog.Canceled ? null : field.Text;
    }

    private void ToggleRemote()
    {
        _settings.AllowRemoteConnections = !_settings.AllowRemoteConnections;
        SettingsStore.Save(_settings);
        _ = _remoteManager.ApplyAsync();
        _pendingLog.Enqueue(_settings.AllowRemoteConnections ? "remote server starting" : "remote server stopping");
    }

    private void ToggleBroadcast()
    {
        _settings.BroadcastServer = !_settings.BroadcastServer;
        SettingsStore.Save(_settings);
        _ = _remoteManager.ApplyAsync();
        _pendingLog.Enqueue(_settings.BroadcastServer ? "mDNS broadcast on" : "mDNS broadcast off");
    }

    private void ShowPairing()
    {
        if (!_settings.AllowRemoteConnections)
        {
            _pendingLog.Enqueue("[pairing] enable remote connections first");
            return;
        }

        var pin = _remoteManager.Pairing.BeginPairing();
        using Dialog dialog = new() { Title = "Pair a Device", Width = 46, Height = 9 };
        Label info = new() { X = 1, Y = 0, Text = "Enter this PIN on the device:" };
        Label pinLabel = new() { X = 1, Y = 2, Text = $"        {pin[..3]} {pin[3..]}" };
        Label status = new()
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Text = $"Waiting for a device… PIN valid {PairingManager.PinLifetime.TotalMinutes:0} min.",
        };
        dialog.Add(info, pinLabel, status);
        dialog.AddButton(new Button { Title = "_Close", IsDefault = true });

        Action<PairedDevice> onPaired = device =>
            _app.Invoke(() => status.Text = $"Paired “{device.Name}” ✔");
        _remoteManager.Pairing.DevicePaired += onPaired;
        try
        {
            App!.Run(dialog);
        }
        finally
        {
            _remoteManager.Pairing.DevicePaired -= onPaired;
            _remoteManager.Pairing.CancelPairing();
        }
    }

    private void ShowPairedDevices()
    {
        while (true)
        {
            var devices = _remoteManager.Pairing.Devices;
            if (devices.Count == 0)
            {
                MessageBox.Query(App!, "Paired Devices", "No devices are paired.", "OK");
                return;
            }

            using Dialog dialog = new() { Title = "Paired Devices" };
            ObservableCollection<string> rows = [.. devices.Select(d => $"{d.Name} — {d.DeviceModel}")];
            ListView list = new() { Width = 50, Height = Math.Min(devices.Count, 10) };
            list.SetSource(rows);
            list.Value = 0;
            dialog.Add(list);
            dialog.AddButton(new Button { Title = "_Close" });
            dialog.AddButton(new Button { Title = "_Forget Selected" });
            App!.Run(dialog);

            if (dialog.Canceled || list.Value is not { } index || index >= devices.Count)
            {
                return;
            }

            _remoteManager.RevokeDevice(devices[index].Id);
            _pendingLog.Enqueue($"[pairing] forgot “{devices[index].Name}”");
        }
    }

    private void ShowKeys()
    {
        MessageBox.Query(
            App!,
            "Keys",
            """
            F5      Previous preset
            F6      Next preset
            F7      Toggle preset lock
            F8      Toggle shuffle
            Enter   Play selected preset
            F9      Open the menu
            Ctrl+Q  Quit
            """,
            "OK");
    }
}
