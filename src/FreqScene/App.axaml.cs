using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using FreqScene.Remote.Server;

namespace FreqScene;

public partial class App : Application
{
    private readonly List<(DisplayMode Mode, NativeMenuItem Item)> _modeItems = [];
    private readonly List<(int Percent, NativeMenuItem Item)> _resolutionItems = [];
    private readonly List<(int Cap, NativeMenuItem Item)> _frameRateItems = [];
    private AppSettings _settings = new();
    private NativeMenu? _audioMenu;
    private NativeMenu? _displayMenu;
    private NativeMenuItem? _wallpaperTransparencyItem;
    private NativeMenuItem? _remoteAllowItem;
    private NativeMenuItem? _remoteBroadcastItem;
    private NativeMenuItem? _remoteStatusItem;
    private NativeMenuItem? _pairItem;
    private NativeMenu? _pairedDevicesMenu;
    private PairingWindow? _pairingWindow;
    private PairPinDialog? _pairPinDialog;
    private NativeMenu? _connectMenu;
    private NativeMenuItem? _clientStatusItem;
    private NativeMenuItem? _stopItem;
    private RemoteServerManager? _remoteManager;
    private RemoteClientManager? _clientManager;
    private MdnsBrowser? _mdnsBrowser;
    private VisualizerCoordinator? _coordinator;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private object? _activeWindow;
    private DisplayMode _mode = DisplayMode.Window;
    private PlaylistEditorWindow? _playlistWindow;
    private bool _quitting;
    private bool _switchingMode;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;


            desktop.Exit += (_, _) =>
            {
                (_activeWindow as INativeVisualizerWindow)?.Close();
                _mdnsBrowser?.Dispose();
                _clientManager?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                _remoteManager?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
                _coordinator?.Dispose();
            };
            _coordinator = new VisualizerCoordinator { UiDispatcher = AvaloniaUiDispatcher.Instance };
            _settings = SettingsStore.Load();
            _settings.RenderScalePercent = QualityOptions.NormalizeRenderScale(_settings.RenderScalePercent);
            _settings.FrameRateCap = QualityOptions.NormalizeFrameRate(_settings.FrameRateCap);
            _mode = DisplayModes.Normalize(_settings.DisplayMode);
            _coordinator.RenderScalePercent = _settings.RenderScalePercent;
            _coordinator.FrameRateCap = _settings.FrameRateCap;
            _coordinator.WallpaperTransparency = _settings.WallpaperTransparency;
            if (_settings.VisualizerStopped)
            {
                _coordinator.SetStopped(true);
            }

            _remoteManager = new RemoteServerManager(_coordinator, _settings);
            _remoteManager.ClientsChanged += () => Dispatcher.UIThread.Post(UpdateRemoteStatus);
            _remoteManager.StatusChanged += message => Console.WriteLine($"[remote] {message}");
            _remoteManager.Pairing.DevicesChanged += () => Dispatcher.UIThread.Post(BuildPairedDevicesMenu);
            if (_settings.AllowRemoteConnections)
            {
                _ = _remoteManager.ApplyAsync();
            }

            _clientManager = new RemoteClientManager(_coordinator);
            _clientManager.StateChanged += () => Dispatcher.UIThread.Post(UpdateClientStatus);
            _clientManager.StatusChanged += message => Console.WriteLine($"[remote client] {message}");
            _clientManager.PairingRequired += () => Dispatcher.UIThread.Post(ShowPairPinDialog);
            try
            {
                _mdnsBrowser = new MdnsBrowser();
                _mdnsBrowser.ServersChanged += () => Dispatcher.UIThread.Post(BuildConnectMenu);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[remote client] mDNS browse unavailable: {ex.Message}");
            }

            SetupTrayIcon(desktop);
            if (!_settings.VisualizerStopped)
            {
                ApplyMode(_mode, persist: false);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is OperationCanceledException
            && e.Exception.StackTrace?.Contains("DBusTrayIconImpl", StringComparison.Ordinal) == true)
        {
            e.Handled = true;
        }
    }

    private void ApplyMode(DisplayMode mode, bool persist = true)
    {
        if (_coordinator is null || _desktop is null)
        {
            return;
        }

        mode = DisplayModes.Normalize(mode);
        _mode = mode;

        CloseActiveWindow();

        if (!_settings.VisualizerStopped)
        {
            if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                INativeVisualizerWindow native = OperatingSystem.IsMacOS()
                    ? new MacVisualizerWindow(_coordinator, mode, _settings.PreferredDisplay)
                    : OperatingSystem.IsWindows()
                        ? new WindowsVisualizerWindow(_coordinator, mode, _settings.PreferredDisplay)
                        : new LinuxVisualizerWindow(_coordinator, mode, _settings.PreferredDisplay);
                _activeWindow = native;
                native.Show();
            }
            else
            {
                var window = CreateMainWindow(_coordinator);
                _activeWindow = window;
                _desktop.MainWindow = window;
                window.Show();
            }
        }

        foreach (var (itemMode, item) in _modeItems)
        {
            item.IsChecked = itemMode == mode;
        }

        BuildDisplayMenu();

        if (persist)
        {
            _settings.DisplayMode = mode;
            SettingsStore.Save(_settings);
        }
    }

    private void CloseActiveWindow()
    {
        if (_activeWindow is Window previous)
        {
            _switchingMode = true;
            try
            {
                previous.Close();
            }
            finally
            {
                _switchingMode = false;
            }
        }
        else if (_activeWindow is INativeVisualizerWindow previousNative)
        {
            previousNative.Close();
        }

        _activeWindow = null;
    }

    private void ApplyStopped(bool stopped)
    {
        if (_settings.VisualizerStopped == stopped)
        {
            return;
        }

        _settings.VisualizerStopped = stopped;
        SettingsStore.Save(_settings);
        if (_stopItem is not null)
        {
            _stopItem.Header = StopItemLabel();
        }

        if (stopped)
        {
            CloseActiveWindow();
            _coordinator?.SetStopped(true);
        }
        else
        {
            _coordinator?.SetStopped(false);
            ApplyMode(_mode, persist: false);
        }
    }

    private string StopItemLabel() =>
        _settings.VisualizerStopped ? "Start Visualization" : "Stop Visualization";

    private MainWindow CreateMainWindow(VisualizerCoordinator coordinator)
    {
        var window = new MainWindow(coordinator);
        window.Closing += (_, e) =>
        {
            // Closing the window is a hide, not a mode change; the tray icon brings it back.
            if (!_quitting && !_switchingMode)
            {
                e.Cancel = true;
                window.Hide();
            }
        };
        window.Closed += (_, _) => coordinator.DetachControl(window.Visualizer);
        return window;
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _audioMenu = new NativeMenu();
        BuildAudioMenu();
        var audioItem = new NativeMenuItem("Audio Source") { Menu = _audioMenu };

        var playlistItem = new NativeMenuItem("Playlist…");
        playlistItem.Click += (_, _) => ShowPlaylistEditor();

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) =>
        {
            _quitting = true;
            desktop.Shutdown();
        };

        _stopItem = new NativeMenuItem(StopItemLabel());
        _stopItem.Click += (_, _) =>
            Dispatcher.UIThread.Post(() => ApplyStopped(!_settings.VisualizerStopped));

        var menu = new NativeMenu
        {
            Items = { _stopItem, new NativeMenuItemSeparator(), audioItem, playlistItem },
        };

        if (DisplayModes.Available.Count > 1)
        {
            menu.Items.Add(BuildDisplayModeItem());
            _displayMenu = new NativeMenu();
            menu.Items.Add(new NativeMenuItem("Display") { Menu = _displayMenu });
        }

        if (DisplayModes.Available.Contains(DisplayMode.Wallpaper))
        {
            menu.Items.Add(BuildWallpaperTransparencyItem());
        }

        menu.Items.Add(BuildResolutionItem());
        menu.Items.Add(BuildFrameRateItem());
        menu.Items.Add(BuildRemoteItem());
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        var trayIcon = new TrayIcon
        {
            Icon = CreateTrayIconImage(),
            ToolTipText = "FreqScene",
            Menu = menu,
        };
        trayIcon.Clicked += (_, _) =>
        {
            if (_activeWindow is MainWindow window)
            {
                window.Show();
                window.Activate();
            }
            else if (_activeWindow is INativeVisualizerWindow native && _mode == DisplayMode.Window)
            {
                native.Show();
            }
        };
        TrayIcon.SetIcons(this, [trayIcon]);
    }

    private NativeMenuItem BuildDisplayModeItem()
    {
        var modeMenu = new NativeMenu();
        foreach (var mode in DisplayModes.Available)
        {
            var item = new NativeMenuItem(DisplayModes.Label(mode))
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = mode == _mode,
            };
            var target = mode;
            item.Click += (_, _) => Dispatcher.UIThread.Post(() => ApplyMode(target));
            modeMenu.Items.Add(item);
            _modeItems.Add((mode, item));
        }

        return new NativeMenuItem("Display Mode") { Menu = modeMenu };
    }

    private void BuildDisplayMenu()
    {
        if (_displayMenu is null)
        {
            return;
        }

        _displayMenu.Items.Clear();
        var displays = DisplayTargets.List();
        var selected = displays.FirstOrDefault(d => d.Key == _settings.PreferredDisplay)
            ?? displays.FirstOrDefault(d => d.IsPrimary)
            ?? displays.FirstOrDefault();
        foreach (var display in displays)
        {
            var item = new NativeMenuItem(display.Label)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = display == selected,
            };
            var target = display.Key;
            item.Click += (_, _) => Dispatcher.UIThread.Post(() => ApplyDisplayTarget(target));
            _displayMenu.Items.Add(item);
        }
    }

    private void ApplyDisplayTarget(string key)
    {
        _settings.PreferredDisplay = key;
        SettingsStore.Save(_settings);

        if (_mode is DisplayMode.Overlay or DisplayMode.Wallpaper)
        {
            // Recreating the window moves the visualizer; ApplyMode also re-ticks the menu.
            ApplyMode(_mode, persist: false);
        }
        else
        {
            BuildDisplayMenu();
        }
    }

    private NativeMenuItem BuildWallpaperTransparencyItem()
    {
        var item = new NativeMenuItem("Wallpaper Transparency")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.WallpaperTransparency,
        };
        item.Click += (_, _) =>
            Dispatcher.UIThread.Post(() => ApplyWallpaperTransparency(!_settings.WallpaperTransparency));
        _wallpaperTransparencyItem = item;
        return item;
    }

    private void ApplyWallpaperTransparency(bool enabled)
    {
        _settings.WallpaperTransparency = enabled;
        if (_coordinator is not null)
        {
            _coordinator.WallpaperTransparency = enabled;
        }

        if (_wallpaperTransparencyItem is not null)
        {
            _wallpaperTransparencyItem.IsChecked = enabled;
        }

        SettingsStore.Save(_settings);

        if (_mode == DisplayMode.Wallpaper)
        {
            ApplyMode(_mode, persist: false);
        }
    }

    private NativeMenuItem BuildResolutionItem()
    {
        var menu = new NativeMenu();
        foreach (var percent in QualityOptions.RenderScalePercents)
        {
            var item = new NativeMenuItem($"{percent}%")
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = percent == _settings.RenderScalePercent,
            };
            var target = percent;
            item.Click += (_, _) => Dispatcher.UIThread.Post(() => ApplyRenderScale(target));
            menu.Items.Add(item);
            _resolutionItems.Add((percent, item));
        }

        return new NativeMenuItem("Resolution") { Menu = menu };
    }

    private NativeMenuItem BuildFrameRateItem()
    {
        var menu = new NativeMenu();
        foreach (var cap in QualityOptions.FrameRateCaps)
        {
            var item = new NativeMenuItem(QualityOptions.FrameRateLabel(cap))
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = cap == _settings.FrameRateCap,
            };
            var target = cap;
            item.Click += (_, _) => Dispatcher.UIThread.Post(() => ApplyFrameRateCap(target));
            menu.Items.Add(item);
            _frameRateItems.Add((cap, item));
        }

        return new NativeMenuItem("Frame Rate") { Menu = menu };
    }

    private void ApplyRenderScale(int percent)
    {
        percent = QualityOptions.NormalizeRenderScale(percent);
        _settings.RenderScalePercent = percent;
        if (_coordinator is not null)
        {
            _coordinator.RenderScalePercent = percent;
        }

        foreach (var (itemPercent, item) in _resolutionItems)
        {
            item.IsChecked = itemPercent == percent;
        }

        SettingsStore.Save(_settings);
    }

    private void ApplyFrameRateCap(int cap)
    {
        cap = QualityOptions.NormalizeFrameRate(cap);
        _settings.FrameRateCap = cap;
        if (_coordinator is not null)
        {
            _coordinator.FrameRateCap = cap;
        }

        foreach (var (itemCap, item) in _frameRateItems)
        {
            item.IsChecked = itemCap == cap;
        }

        SettingsStore.Save(_settings);
    }

    private NativeMenuItem BuildRemoteItem()
    {
        _remoteAllowItem = new NativeMenuItem("Allow Remote Connections")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.AllowRemoteConnections,
        };
        _remoteAllowItem.Click += (_, _) =>
            Dispatcher.UIThread.Post(() => ApplyRemoteSettings(!_settings.AllowRemoteConnections, _settings.BroadcastServer));

        _remoteBroadcastItem = new NativeMenuItem("Broadcast on Local Network")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _settings.BroadcastServer,
        };
        _remoteBroadcastItem.Click += (_, _) =>
            Dispatcher.UIThread.Post(() => ApplyRemoteSettings(_settings.AllowRemoteConnections, !_settings.BroadcastServer));

        _remoteStatusItem = new NativeMenuItem("No devices connected") { IsEnabled = false };

        _pairItem = new NativeMenuItem("Pair a Device…") { IsEnabled = _settings.AllowRemoteConnections };
        _pairItem.Click += (_, _) => Dispatcher.UIThread.Post(ShowPairingWindow);

        _pairedDevicesMenu = new NativeMenu();
        BuildPairedDevicesMenu();

        _connectMenu = new NativeMenu();
        BuildConnectMenu();
        _clientStatusItem = new NativeMenuItem("Not mirroring") { IsEnabled = false };

        var menu = new NativeMenu
        {
            Items =
            {
                _remoteAllowItem, _remoteBroadcastItem, new NativeMenuItemSeparator(),
                _pairItem, new NativeMenuItem("Paired Devices") { Menu = _pairedDevicesMenu },
                _remoteStatusItem,
                new NativeMenuItemSeparator(),
                new NativeMenuItem("Connect to Host") { Menu = _connectMenu }, _clientStatusItem,
            },
        };
        return new NativeMenuItem("Remote") { Menu = menu };
    }

    private void ShowPairingWindow()
    {
        if (_remoteManager is null)
        {
            return;
        }

        if (_pairingWindow is not null)
        {
            _pairingWindow.Activate();
            return;
        }

        _pairingWindow = new PairingWindow(_remoteManager);
        _pairingWindow.Closed += (_, _) => _pairingWindow = null;
        _pairingWindow.Show();
        _pairingWindow.Activate();
    }

    private void BuildPairedDevicesMenu()
    {
        if (_pairedDevicesMenu is null)
        {
            return;
        }

        _pairedDevicesMenu.Items.Clear();
        var devices = _remoteManager?.Pairing.Devices ?? [];
        if (devices.Count == 0)
        {
            _pairedDevicesMenu.Items.Add(new NativeMenuItem("No paired devices") { IsEnabled = false });
            return;
        }

        foreach (var device in devices)
        {
            var deviceMenu = new NativeMenu
            {
                Items = { new NativeMenuItem($"{device.DeviceModel} — paired {device.PairedAt:d}") { IsEnabled = false } },
            };
            var forgetItem = new NativeMenuItem("Forget This Device");
            var targetId = device.Id;
            forgetItem.Click += (_, _) => Dispatcher.UIThread.Post(() => _remoteManager?.RevokeDevice(targetId));
            deviceMenu.Items.Add(forgetItem);
            _pairedDevicesMenu.Items.Add(new NativeMenuItem(device.Name) { Menu = deviceMenu });
        }
    }

    private void ShowPairPinDialog()
    {
        if (_clientManager is null)
        {
            return;
        }

        if (_pairPinDialog is not null)
        {
            _pairPinDialog.Activate();
            return;
        }

        var dialog = new PairPinDialog(_clientManager.HostName);
        dialog.PairRequested += async pin =>
        {
            try
            {
                await _clientManager.PairAsync(pin);
                dialog.Close();
            }
            catch (Remote.Client.PairingException ex)
            {
                dialog.ShowError(ex.Message);
            }
            catch (Exception ex)
            {
                dialog.ShowError($"Pairing failed: {ex.Message}");
            }
        };
        dialog.Closed += (_, _) => _pairPinDialog = null;
        _pairPinDialog = dialog;
        dialog.Show();
        dialog.Activate();
    }

    private void BuildConnectMenu()
    {
        if (_connectMenu is null)
        {
            return;
        }

        _connectMenu.Items.Clear();

        var servers = _mdnsBrowser?.Servers ?? [];
        foreach (var server in servers)
        {
            if (string.Equals(server.InstanceName, _remoteManager?.ServerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = new NativeMenuItem(server.IsCompatible
                ? server.InstanceName
                : $"{server.InstanceName} (incompatible v{server.ProtocolVersion})")
            {
                ToggleType = MenuItemToggleType.Radio,
                IsEnabled = server.IsCompatible,
                IsChecked = _clientManager?.IsActive == true &&
                            string.Equals(_clientManager.HostName, server.InstanceName, StringComparison.OrdinalIgnoreCase),
            };
            var target = server;
            item.Click += (_, _) => Dispatcher.UIThread.Post(() => ConnectToDiscovered(target));
            _connectMenu.Items.Add(item);
        }

        if (_connectMenu.Items.Count > 0)
        {
            _connectMenu.Items.Add(new NativeMenuItemSeparator());
        }

        var manualItem = new NativeMenuItem("Connect to Address…");
        manualItem.Click += (_, _) => Dispatcher.UIThread.Post(ShowConnectDialog);
        _connectMenu.Items.Add(manualItem);

        var disconnectItem = new NativeMenuItem("Disconnect") { IsEnabled = _clientManager?.IsActive == true };
        disconnectItem.Click += (_, _) => Dispatcher.UIThread.Post(() => _ = _clientManager?.DisconnectAsync());
        _connectMenu.Items.Add(disconnectItem);
    }

    private void ConnectToDiscovered(DiscoveredServer server)
    {
        StopServerForClientMode();
        var name = server.InstanceName;
        _ = _clientManager?.ConnectAsync(
            server.Uri,
            name,
            _ => Task.FromResult(_mdnsBrowser?.Resolve(name)));
        UpdateClientStatus();
    }

    private void ShowConnectDialog()
    {
        var dialog = new ConnectDialog(_settings.LastRemoteAddress);
        dialog.ConnectRequested += (host, port) =>
        {
            _settings.LastRemoteAddress = $"{host}:{port}";
            SettingsStore.Save(_settings);

            StopServerForClientMode();
            var uriHost = Uri.CheckHostName(host) == UriHostNameType.IPv6 ? $"[{host}]" : host;
            _ = _clientManager?.ConnectAsync(new Uri($"http://{uriHost}:{port}"), $"{host}:{port}");
            UpdateClientStatus();
        };
        dialog.Show();
        dialog.Activate();
    }

    /// <summary>Client and host modes are mutually exclusive; connecting turns the server off.</summary>
    private void StopServerForClientMode()
    {
        if (_settings.AllowRemoteConnections)
        {
            ApplyRemoteSettings(false, _settings.BroadcastServer);
        }
    }

    private void UpdateClientStatus()
    {
        if (_clientStatusItem is null || _clientManager is null)
        {
            return;
        }

        _clientStatusItem.Header = _clientManager.State switch
        {
            null or Remote.Client.RemoteSessionState.Stopped => "Not mirroring",
            Remote.Client.RemoteSessionState.Connecting => $"Connecting to “{_clientManager.HostName}”…",
            Remote.Client.RemoteSessionState.Reconnecting => $"Reconnecting to “{_clientManager.HostName}”…",
            Remote.Client.RemoteSessionState.PairingRequired => $"Pairing required for “{_clientManager.HostName}”",
            _ => _clientManager.CurrentPresetName is { } preset
                ? $"Mirroring “{_clientManager.HostName}” — {preset}"
                : $"Mirroring “{_clientManager.HostName}”",
        };
        BuildConnectMenu();
    }

    private void ApplyRemoteSettings(bool allow, bool broadcast)
    {
        // Hosting and mirroring are mutually exclusive; enabling the server disconnects the client.
        if (allow && _clientManager?.IsActive == true)
        {
            _ = _clientManager.DisconnectAsync();
        }

        _settings.AllowRemoteConnections = allow;
        _settings.BroadcastServer = broadcast;
        if (_remoteAllowItem is not null)
        {
            _remoteAllowItem.IsChecked = allow;
        }

        if (_remoteBroadcastItem is not null)
        {
            _remoteBroadcastItem.IsChecked = broadcast;
        }

        if (_pairItem is not null)
        {
            _pairItem.IsEnabled = allow;
        }

        if (!allow)
        {
            _pairingWindow?.Close();
        }

        SettingsStore.Save(_settings);
        _ = _remoteManager?.ApplyAsync();
        UpdateRemoteStatus();
    }

    private void UpdateRemoteStatus()
    {
        if (_remoteStatusItem is null)
        {
            return;
        }

        var count = _remoteManager?.ClientCount ?? 0;
        _remoteStatusItem.Header = count switch
        {
            0 => "No devices connected",
            1 => "1 device connected",
            _ => $"{count} devices connected",
        };
    }

    private void ShowPlaylistEditor()
    {
        if (_coordinator is null)
        {
            return;
        }

        if (_playlistWindow is null)
        {
            _playlistWindow = new PlaylistEditorWindow(_coordinator);
            _playlistWindow.Closing += (_, e) =>
            {
                if (!_quitting)
                {
                    e.Cancel = true;
                    _playlistWindow.Hide();
                }
            };
        }

        _playlistWindow.Show();
        _playlistWindow.Activate();
    }

    private void BuildAudioMenu()
    {
        if (_audioMenu is null || _coordinator is null)
        {
            return;
        }

        _audioMenu.Items.Clear();
        foreach (var source in _coordinator.AudioSources)
        {
            var item = new NativeMenuItem(source)
            {
                ToggleType = MenuItemToggleType.Radio,
                IsChecked = source == _coordinator.SelectedAudioSource,
            };
            item.Click += (_, _) =>
            {
                _coordinator.SelectAudioSource(source);
                BuildAudioMenu();
            };
            _audioMenu.Items.Add(item);
        }
    }

    private static WindowIcon CreateTrayIconImage()
    {
        using var stream = AssetLoader.Open(new Uri("avares://FreqScene/Assets/TrayIcon.png"));
        return new WindowIcon(new Bitmap(stream));
    }
}