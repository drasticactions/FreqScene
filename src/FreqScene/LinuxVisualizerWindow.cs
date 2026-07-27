using Microsoft.Extensions.Logging;

namespace FreqScene;

internal sealed class LinuxVisualizerWindow : INativeVisualizerWindow
{
    private readonly VisualizerCoordinator _coordinator;
    private readonly LinuxVisualizerHost _host;
    private readonly Action<int> _onRenderScaleChanged;
    private bool _closed;

    public LinuxVisualizerWindow(
        VisualizerCoordinator coordinator, DisplayMode mode, string? displayKey, ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger<LinuxVisualizerWindow>();
        _coordinator = coordinator;
        _host = new LinuxVisualizerHost(
            mode, coordinator.WallpaperTransparency, displayKey, AvaloniaUiDispatcher.Instance, loggerFactory)
        {
            RenderScale = coordinator.RenderScalePercent / 100.0,
        };
        _host.InitializationFailed += (_, ex) => log.LogError(ex, "visualizer init failed");
        _onRenderScaleChanged = percent => _host.RenderScale = percent / 100.0;
        coordinator.RenderScaleChanged += _onRenderScaleChanged;
        coordinator.AttachControl(_host);
    }

    public void Show()
    {
        if (_closed)
        {
            return;
        }

        _host.Start();
        _host.RequestShow();
    }

    public void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _coordinator.RenderScaleChanged -= _onRenderScaleChanged;
        _coordinator.DetachControl(_host);
        _host.Dispose();
    }
}
