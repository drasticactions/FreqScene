using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace FreqScene;

public partial class App : Application
{
    private NativeMenu? _audioMenu;
    private VisualizerCoordinator? _coordinator;
    private MainWindow? _mainWindow;
    private PlaylistEditorWindow? _playlistWindow;
    private bool _quitting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) => _coordinator?.Dispose();
            _coordinator = new VisualizerCoordinator();
            _mainWindow = new MainWindow(_coordinator);
            _mainWindow.Closing += (_, e) =>
            {
                if (!_quitting)
                {
                    e.Cancel = true;
                    _mainWindow.Hide();
                }
            };
            desktop.MainWindow = _mainWindow;

            SetupTrayIcon(desktop);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _audioMenu = new NativeMenu();
        BuildAudioMenu();
        var audioItem = new NativeMenuItem("Audio Source") { Menu = _audioMenu };
        
        var showItem = new NativeMenuItem("Show Visualizer");
        showItem.Click += (_, _) =>
        {
            _mainWindow?.Show();
            _mainWindow?.Activate();
        };

        var playlistItem = new NativeMenuItem("Playlist…");
        playlistItem.Click += (_, _) => ShowPlaylistEditor();

        var hideItem = new NativeMenuItem("Hide Visualizer");
        hideItem.Click += (_, _) =>
        {
            _mainWindow?.Hide();
        };

        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) =>
        {
            _quitting = true;
            desktop.Shutdown();
        };

        var menu = new NativeMenu
        {
            Items =
            {
                audioItem,
                playlistItem,
                new NativeMenuItemSeparator(),
                showItem,
                hideItem,
                quitItem,
            },
        };

        var trayIcon = new TrayIcon
        {
            Icon = CreateTrayIconImage(),
            ToolTipText = "projectM visualizer",
            Menu = menu,
        };
        TrayIcon.SetIcons(this, [trayIcon]);
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