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
    private bool _quitting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _coordinator = new VisualizerCoordinator();
            _mainWindow = new MainWindow(_coordinator);
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
                new NativeMenuItemSeparator(),
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