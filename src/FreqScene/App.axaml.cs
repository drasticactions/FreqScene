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
    private NativeMenu? _audioMenu;
    private VisualizerCoordinator? _coordinator;
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private Window? _activeWindow;
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
            desktop.Exit += (_, _) => _coordinator?.Dispose();
            _coordinator = new VisualizerCoordinator();
            _mode = DisplayModes.Normalize(SettingsStore.Load().DisplayMode);

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

        if (_activeWindow is { } previous)
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

        Window window = mode == DisplayMode.Window
            ? CreateMainWindow(_coordinator)
            : new OverlayWindow(_coordinator, mode == DisplayMode.Wallpaper);

        _activeWindow = window;
        _desktop.MainWindow = window as MainWindow;
        window.Show();

        foreach (var (itemMode, item) in _modeItems)
        {
            item.IsChecked = itemMode == mode;
        }

        if (persist)
        {
            SettingsStore.Save(new AppSettings { DisplayMode = mode });
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
        }

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