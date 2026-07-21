using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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
            desktop.Exit += (_, _) =>
            {
                (_activeWindow as INativeVisualizerWindow)?.Close();
                _coordinator?.Dispose();
            };
            _coordinator = new VisualizerCoordinator();
            _settings = SettingsStore.Load();
            _settings.RenderScalePercent = QualityOptions.NormalizeRenderScale(_settings.RenderScalePercent);
            _settings.FrameRateCap = QualityOptions.NormalizeFrameRate(_settings.FrameRateCap);
            _mode = DisplayModes.Normalize(_settings.DisplayMode);
            _coordinator.RenderScalePercent = _settings.RenderScalePercent;
            _coordinator.FrameRateCap = _settings.FrameRateCap;
            _coordinator.WallpaperTransparency = _settings.WallpaperTransparency;

            SetupTrayIcon(desktop);
            ApplyMode(_mode, persist: false);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyMode(DisplayMode mode, bool persist = true)
    {
        if (_coordinator is null || _desktop is null)
        {
            return;
        }

        mode = DisplayModes.Normalize(mode);
        _mode = mode;

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

        var menu = new NativeMenu
        {
            Items = { audioItem, playlistItem },
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
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        var trayIcon = new TrayIcon
        {
            Icon = CreateTrayIconImage(),
            ToolTipText = "projectM visualizer",
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
        var bitmap = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(20, 20, 28)), null, new Point(16, 16), 15, 15);
            var text = new FormattedText(
                "F",
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.Bold),
                18,
                new SolidColorBrush(Color.FromRgb(90, 220, 130)));
            context.DrawText(text, new Point(16 - (text.Width / 2), 16 - (text.Height / 2)));
        }

        return new WindowIcon(bitmap);
    }
}