using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace FreqScene;

public partial class App : Application
{
    private VisualizerCoordinator? _coordinator;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _coordinator = new VisualizerCoordinator();
            desktop.MainWindow = new MainWindow(_coordinator);
        }

        base.OnFrameworkInitializationCompleted();
    }
}