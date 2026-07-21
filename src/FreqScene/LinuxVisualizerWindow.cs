namespace FreqScene;

internal sealed class LinuxVisualizerWindow : INativeVisualizerWindow
{
    private readonly VisualizerCoordinator _coordinator;
    private readonly LinuxVisualizerHost _host;
    private readonly Action<int> _onRenderScaleChanged;
    private bool _closed;

    public LinuxVisualizerWindow(VisualizerCoordinator coordinator, DisplayMode mode, string? displayKey)
    {
        _coordinator = coordinator;
        _host = new LinuxVisualizerHost(mode, coordinator.WallpaperTransparency, displayKey)
        {
            RenderScale = coordinator.RenderScalePercent / 100.0,
        };
        _host.InitializationFailed += (_, ex) =>
            System.Diagnostics.Trace.TraceError($"[native] visualizer init failed: {ex}");
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
