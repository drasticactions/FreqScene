using Avalonia.Controls;

namespace FreqScene;

public partial class MainWindow : Window
{
    private readonly VisualizerCoordinator? _coordinator;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(VisualizerCoordinator coordinator)
    {
        _coordinator = coordinator;
        InitializeComponent();
        coordinator.AttachControl(Visualizer);
    }
}