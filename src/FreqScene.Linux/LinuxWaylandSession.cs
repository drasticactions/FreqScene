using System.Runtime.InteropServices;
using FreqScene.WaylandProtocols;
using Mesa.Egl;
using Wayland;
using Wayland.Egl;
using Wayland.Native;

namespace FreqScene;

public sealed class LinuxWaylandSession : ILinuxGlSession
{
    private const uint BtnLeft = 0x110;
    private const uint DoubleClickMs = 400;
    private const int ResizeMargin = 8;
    private const int DefaultWindowSize = 640;

    private readonly DisplayMode _mode;
    private readonly bool _fullscreen;
    private readonly WlDisplay _display;
    private readonly WlRegistry _registry;

    private readonly List<OutputEntry> _outputs = [];

    private WlCompositor? _compositor;
    private XdgWmBase? _wmBase;
    private ZwlrLayerShellV1? _layerShell;
    private WlSeat? _seat;
    private WlPointer? _pointer;
    private OutputEntry? _selectedOutput;
    private WlSurface? _surface;
    private XdgSurface? _xdgSurface;
    private XdgToplevel? _toplevel;
    private ZwlrLayerSurfaceV1? _layerSurface;
    private WlEglWindow? _eglWindow;

    private int _logicalWidth;
    private int _logicalHeight;
    private int _pendingWidth;
    private int _pendingHeight;
    private int _scale = 1;
    private int _appliedScale;
    private int _appliedWidth;
    private int _appliedHeight;
    private double _refreshRate;
    private bool _configured;
    private bool _visible;
    private bool _maximized;
    private volatile bool _showRequested;
    private volatile bool _closedByCompositor;

    private double _pointerX;
    private double _pointerY;
    private uint _lastClickTime;
    private double _lastClickX;
    private double _lastClickY;

    public LinuxWaylandSession(DisplayMode mode, string? outputKey, bool fullscreen = false)
    {
        _mode = mode;
        _fullscreen = fullscreen;
        _display = WlDisplay.Connect();
        try
        {
            _registry = _display.GetRegistry();
            _registry.Global += OnGlobal;

            // For whatever reason, we need to do this to get all the values.
            _display.Roundtrip();
            _display.Roundtrip();

            if (_compositor is null)
            {
                throw new InvalidOperationException("The Wayland compositor global is missing.");
            }

            _selectedOutput = _outputs.Find(o => o.Key == outputKey) ?? _outputs.FirstOrDefault();
            if (_selectedOutput is not null)
            {
                _scale = _selectedOutput.Scale;
                _refreshRate = _selectedOutput.RefreshRate;
            }

            if (mode == DisplayMode.Window)
            {
                CreateToplevel();
            }
            else
            {
                CreateLayerSurface(mode);
            }

            while (!_configured)
            {
                _display.Dispatch();
            }

            _visible = true;
            _eglWindow = new WlEglWindow(_surface!, _logicalWidth * _scale, _logicalHeight * _scale);
            _surface!.SetBufferScale(_scale);
            _appliedScale = _scale;
            _appliedWidth = _logicalWidth;
            _appliedHeight = _logicalHeight;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IntPtr DisplayHandle => _display.RawHandle;

    public IntPtr EglWindowHandle => _eglWindow?.RawHandle ?? IntPtr.Zero;

    public EglPlatform EglPlatform => EglPlatform.Wayland;

    public IntPtr NativeDisplayHandle => _display.RawHandle;

    public IntPtr NativeWindowHandle => _eglWindow?.RawHandle ?? IntPtr.Zero;

    public int? RequiredNativeVisualId => null;

    public bool Closed => _closedByCompositor;

    public int PixelWidth => _appliedWidth * _appliedScale;

    public int PixelHeight => _appliedHeight * _appliedScale;

    public bool Visible => _visible && _configured;

    public bool ClosedByCompositor => _closedByCompositor;

    public double RefreshRate => _refreshRate > 0 ? _refreshRate : 60;

    public static bool HasLayerShell()
    {
        try
        {
            using var display = WlDisplay.Connect();
            var found = false;
            using var registry = display.GetRegistry();
            registry.Global += (_, e) => found |= e.Interface == "zwlr_layer_shell_v1";
            display.Roundtrip();
            return found;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<DisplayInfo> ListOutputs()
    {
        try
        {
            using var display = WlDisplay.Connect();
            var entries = new List<OutputEntry>();
            using var registry = display.GetRegistry();
            registry.Global += (_, e) =>
            {
                if (e.Interface != "wl_output")
                {
                    return;
                }

                var entry = new OutputEntry { Index = entries.Count };
                var output = registry.Bind<WlOutput>(e.Name, Math.Min(e.Version, 4u));
                output.ModeEvent += (_, args) =>
                {
                    if ((args.Flags & WlOutput.Mode.Current) != 0)
                    {
                        entry.Width = args.Width;
                        entry.Height = args.Height;
                    }
                };
                output.Name += (_, args) => entry.Name = args.Name;
                entry.Output = output;
                entries.Add(entry);
            };
            display.Roundtrip();
            display.Roundtrip();

            var result = new List<DisplayInfo>(entries.Count);
            foreach (var entry in entries)
            {
                var name = entry.Name ?? $"Output {entry.Index + 1}";
                var label = entry.Width > 0 ? $"{name} ({entry.Width}×{entry.Height})" : name;
                // Wayland has no primary-output concept; the first output is the fallback target.
                result.Add(new DisplayInfo(entry.Key, label, entry.Index == 0));
                entry.Output?.Dispose();
            }

            return result;
        }
        catch
        {
            return [];
        }
    }

    public void RequestShow() => _showRequested = true;

    public void AfterSwap(IntPtr eglDisplay, IntPtr eglSurface)
    {
        // The compositor presents Wayland buffers; nothing to do here.
    }

    public unsafe void PumpEvents()
    {
        _display.DispatchPending();

        // The wrapper exposes no prepare-read family, and flush must stay
        // silent on a full socket, so this half of the pump uses the raw
        // bindings directly.
        var display = (wl_display*)_display.RawHandle;
        LibWaylandClient.wl_display_flush(display);
        if (LibWaylandClient.wl_display_prepare_read(display) == 0)
        {
            if (LinuxInterop.PollReadable(_display.Fd))
            {
                LibWaylandClient.wl_display_read_events(display);
            }
            else
            {
                LibWaylandClient.wl_display_cancel_read(display);
            }

            _display.DispatchPending();
        }

        if (_showRequested)
        {
            _showRequested = false;
            Remap();
        }
    }

    public void ApplyPendingResize()
    {
        if (_pendingWidth > 0 && _pendingHeight > 0)
        {
            _logicalWidth = _pendingWidth;
            _logicalHeight = _pendingHeight;
            _pendingWidth = 0;
            _pendingHeight = 0;
        }

        if (_eglWindow is null ||
            (_logicalWidth == _appliedWidth && _logicalHeight == _appliedHeight && _scale == _appliedScale))
        {
            return;
        }

        _appliedWidth = _logicalWidth;
        _appliedHeight = _logicalHeight;
        _appliedScale = _scale;
        _surface?.SetBufferScale(_appliedScale);
        _eglWindow.Resize(_appliedWidth * _appliedScale, _appliedHeight * _appliedScale);
    }

    public unsafe void Dispose()
    {
        _pointer?.Dispose();
        _pointer = null;
        _toplevel?.Dispose();
        _toplevel = null;
        _xdgSurface?.Dispose();
        _xdgSurface = null;
        _layerSurface?.Dispose();
        _layerSurface = null;
        _eglWindow?.Dispose();
        _eglWindow = null;
        _surface?.Dispose();
        _surface = null;
        _seat?.Dispose();
        _seat = null;
        foreach (var output in _outputs)
        {
            output.Output?.Dispose();
        }

        _outputs.Clear();
        _selectedOutput = null;
        _wmBase?.Dispose();
        _wmBase = null;
        _layerShell?.Dispose();
        _layerShell = null;
        _compositor?.Dispose();
        _compositor = null;
        _registry?.Dispose();
        // Raw flush: WlDisplay.Flush throws on a dead connection, which must
        // not escape from Dispose.
        LibWaylandClient.wl_display_flush((wl_display*)_display.RawHandle);
        _display.Dispose();
    }

    private void OnGlobal(object? sender, WlRegistry.GlobalEventArgs e)
    {
        switch (e.Interface)
        {
            case "wl_compositor":
                _compositor = _registry.Bind<WlCompositor>(e.Name, Math.Min(e.Version, 4u));
                break;

            case "xdg_wm_base":
            {
                var wmBase = _registry.Bind<XdgWmBase>(e.Name, Math.Min(e.Version, 2u));
                wmBase.Ping += (_, args) => wmBase.Pong(args.Serial);
                _wmBase = wmBase;
                break;
            }

            case "zwlr_layer_shell_v1":
                _layerShell = _registry.Bind<ZwlrLayerShellV1>(e.Name, Math.Min(e.Version, 4u));
                break;

            case "wl_seat" when _mode == DisplayMode.Window && _seat is null:
            {
                var seat = _registry.Bind<WlSeat>(e.Name, Math.Min(e.Version, 5u));
                seat.Capabilities += (_, args) => OnSeatCapabilities(seat, args.Capabilities);
                _seat = seat;
                break;
            }

            case "wl_output":
            {
                var entry = new OutputEntry { Index = _outputs.Count };
                var output = _registry.Bind<WlOutput>(e.Name, Math.Min(e.Version, 4u));
                output.ModeEvent += (_, args) =>
                {
                    if ((args.Flags & WlOutput.Mode.Current) != 0)
                    {
                        entry.Width = args.Width;
                        entry.Height = args.Height;
                        if (args.Refresh > 0)
                        {
                            entry.RefreshRate = args.Refresh / 1000.0;
                            if (entry == _selectedOutput)
                            {
                                _refreshRate = entry.RefreshRate;
                            }
                        }
                    }
                };
                output.Scale += (_, args) =>
                {
                    if (args.Factor > 0)
                    {
                        entry.Scale = args.Factor;
                        if (entry == _selectedOutput)
                        {
                            _scale = args.Factor;
                        }
                    }
                };
                output.Name += (_, args) => entry.Name = args.Name;
                entry.Output = output;
                _outputs.Add(entry);
                break;
            }
        }
    }

    private void CreateToplevel()
    {
        if (_wmBase is null)
        {
            throw new InvalidOperationException("The compositor does not support xdg_wm_base.");
        }

        _logicalWidth = DefaultWindowSize;
        _logicalHeight = DefaultWindowSize;
        _surface = _compositor!.CreateSurface();
        var xdgSurface = _wmBase.GetXdgSurface(_surface);
        xdgSurface.Configure += (_, e) =>
        {
            xdgSurface.AckConfigure(e.Serial);
            _configured = true;
        };
        _xdgSurface = xdgSurface;

        var toplevel = xdgSurface.GetToplevel();
        toplevel.Configure += (_, e) =>
        {
            _maximized = HasState(e.States, XdgToplevel.State.Maximized);
            if (e.Width > 0 && e.Height > 0)
            {
                _pendingWidth = e.Width;
                _pendingHeight = e.Height;
            }
        };
        toplevel.Close += (_, _) => Unmap();
        _toplevel = toplevel;
        _toplevel.SetTitle("FreqScene");
        _toplevel.SetAppId("FreqScene");
        _toplevel.SetMinSize(320, 240);
        if (_fullscreen)
        {
            _toplevel.SetFullscreen(_selectedOutput?.Output);
        }

        _surface.Commit();
    }

    private void CreateLayerSurface(DisplayMode mode)
    {
        if (_layerShell is null)
        {
            throw new InvalidOperationException("The compositor does not support zwlr_layer_shell_v1.");
        }

        _surface = _compositor!.CreateSurface();
        var layer = mode == DisplayMode.Overlay
            ? ZwlrLayerShellV1.Layer.Top
            : WallpaperLayer();
        var layerSurface = _layerShell.GetLayerSurface(
            _surface, _selectedOutput?.Output, layer, "freqscene");
        layerSurface.Configure += (_, e) =>
        {
            layerSurface.AckConfigure(e.Serial);
            if (e.Width > 0 && e.Height > 0)
            {
                _pendingWidth = (int)e.Width;
                _pendingHeight = (int)e.Height;
                if (!_configured)
                {
                    _logicalWidth = (int)e.Width;
                    _logicalHeight = (int)e.Height;
                    _pendingWidth = 0;
                    _pendingHeight = 0;
                }
            }

            _configured = true;
        };
        layerSurface.Closed += (_, _) => _closedByCompositor = true;
        _layerSurface = layerSurface;
        _layerSurface.SetAnchor(
            ZwlrLayerSurfaceV1.Anchor.Top | ZwlrLayerSurfaceV1.Anchor.Bottom |
            ZwlrLayerSurfaceV1.Anchor.Left | ZwlrLayerSurfaceV1.Anchor.Right);
        _layerSurface.SetExclusiveZone(-1);
        _layerSurface.SetKeyboardInteractivity(ZwlrLayerSurfaceV1.KeyboardInteractivity.None);

        // An empty input region makes the surface click-through.
        using (var region = _compositor.CreateRegion())
        {
            _surface.SetInputRegion(region);
        }

        _surface.Commit();
    }

    // KWin puts background layer surfaces in the same stacking layer as the Plasma
    // desktop window and raises that window whenever it is activated, so a background
    // wallpaper disappears behind it after any click on the desktop. The bottom layer
    // stays above the desktop window and below normal windows permanently.
    private static ZwlrLayerShellV1.Layer WallpaperLayer() =>
        (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty)
            .Contains("KDE", StringComparison.OrdinalIgnoreCase)
            ? ZwlrLayerShellV1.Layer.Bottom
            : ZwlrLayerShellV1.Layer.Background;

    private void OnSeatCapabilities(WlSeat seat, WlSeat.Capability capabilities)
    {
        if ((capabilities & WlSeat.Capability.Pointer) != 0 && _pointer is null)
        {
            var pointer = seat.GetPointer();
            pointer.Enter += (_, e) =>
            {
                _pointerX = e.SurfaceX;
                _pointerY = e.SurfaceY;
            };
            pointer.Motion += (_, e) =>
            {
                _pointerX = e.SurfaceX;
                _pointerY = e.SurfaceY;
            };
            pointer.Button += (_, e) => OnPointerButton(e.Serial, e.Time, e.Button, e.State);
            _pointer = pointer;
        }
        else if ((capabilities & WlSeat.Capability.Pointer) == 0 && _pointer is not null)
        {
            _pointer.Dispose();
            _pointer = null;
        }
    }

    private void OnPointerButton(uint serial, uint time, uint button, WlPointer.ButtonState state)
    {
        if (button != BtnLeft || state != WlPointer.ButtonState.Pressed ||
            _toplevel is null || _seat is null)
        {
            return;
        }

        var isDoubleClick = time - _lastClickTime < DoubleClickMs &&
            Math.Abs(_pointerX - _lastClickX) < 5 && Math.Abs(_pointerY - _lastClickY) < 5;
        _lastClickTime = time;
        _lastClickX = _pointerX;
        _lastClickY = _pointerY;

        if (isDoubleClick)
        {
            _lastClickTime = 0;
            if (_maximized)
            {
                _toplevel.UnsetMaximized();
            }
            else
            {
                _toplevel.SetMaximized();
            }

            return;
        }

        var edge = _maximized ? XdgToplevel.ResizeEdge.None : HitTestResizeEdge();
        if (edge != XdgToplevel.ResizeEdge.None)
        {
            _toplevel.Resize(_seat, serial, edge);
        }
        else
        {
            _toplevel.Move(_seat, serial);
        }
    }

    private XdgToplevel.ResizeEdge HitTestResizeEdge()
    {
        var edge = XdgToplevel.ResizeEdge.None;
        if (_pointerY < ResizeMargin)
        {
            edge |= XdgToplevel.ResizeEdge.Top;
        }
        else if (_pointerY >= _logicalHeight - ResizeMargin)
        {
            edge |= XdgToplevel.ResizeEdge.Bottom;
        }

        if (_pointerX < ResizeMargin)
        {
            edge |= XdgToplevel.ResizeEdge.Left;
        }
        else if (_pointerX >= _logicalWidth - ResizeMargin)
        {
            edge |= XdgToplevel.ResizeEdge.Right;
        }

        return edge;
    }

    private static bool HasState(ReadOnlySpan<byte> states, XdgToplevel.State state)
    {
        foreach (var value in MemoryMarshal.Cast<byte, uint>(states))
        {
            if (value == (uint)state)
            {
                return true;
            }
        }

        return false;
    }

    private sealed class OutputEntry
    {
        public WlOutput? Output { get; set; }

        public int Index { get; init; }

        public string? Name { get; set; }

        public int Scale { get; set; } = 1;

        public double RefreshRate { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public string Key => Name ?? $"output-{Index}";
    }

    /// <summary>Hides the window-mode surface; the tray icon maps it again via <see cref="RequestShow"/>.</summary>
    private void Unmap()
    {
        if (_surface is null || !_visible)
        {
            return;
        }

        _surface.Attach(null, 0, 0);
        _surface.Commit();
        _visible = false;
        _configured = false;
    }

    private void Remap()
    {
        if (_surface is null || _visible)
        {
            return;
        }

        // The initial-commit dance again: commit without a buffer, wait for configure,
        // then the next eglSwapBuffers maps the surface.
        _surface.Commit();
        _visible = true;
    }
}
