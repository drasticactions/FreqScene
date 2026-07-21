using System.Diagnostics;
using System.Runtime.InteropServices;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.Wlr.WlrLayerShellUnstableV1;
using NWayland.Protocols.XdgShell;

namespace FreqScene;

internal sealed class LinuxWaylandSession : IDisposable
{
    private const uint BtnLeft = 0x110;
    private const uint DoubleClickMs = 400;
    private const int ResizeMargin = 8;
    private const int DefaultWindowSize = 640;

    private readonly DisplayMode _mode;
    private readonly WlDisplay _display;
    private readonly WlRegistry _registry;

    private WlCompositor? _compositor;
    private XdgWmBase? _wmBase;
    private ZwlrLayerShellV1? _layerShell;
    private WlSeat? _seat;
    private WlPointer? _pointer;
    private WlOutput? _output;
    private WlSurface? _surface;
    private XdgSurface? _xdgSurface;
    private XdgToplevel? _toplevel;
    private ZwlrLayerSurfaceV1? _layerSurface;
    private IntPtr _eglWindow;

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

    public LinuxWaylandSession(DisplayMode mode)
    {
        _mode = mode;
        _display = WlDisplay.Connect();
        try
        {
            _registry = _display.GetRegistry(new WlRegistry.Listener.Relay
            {
                OnGlobal = OnGlobal,
            });

            // For whatever reason, we need to do this to get all the values.
            _display.Roundtrip();
            _display.Roundtrip();

            if (_compositor is null)
            {
                throw new InvalidOperationException("The Wayland compositor global is missing.");
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
                if (_display.Dispatch() < 0)
                {
                    throw new InvalidOperationException("The Wayland connection failed while waiting for the initial configure.");
                }
            }

            _visible = true;
            _eglWindow = LinuxInterop.wl_egl_window_create(
                _surface!.Handle, _logicalWidth * _scale, _logicalHeight * _scale);
            if (_eglWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException("wl_egl_window_create failed.");
            }

            _surface.SetBufferScale(_scale);
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

    public IntPtr DisplayHandle => _display.Handle;

    public IntPtr EglWindowHandle => _eglWindow;

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
            using var registry = display.GetRegistry(new WlRegistry.Listener.Relay
            {
                OnGlobal = (_, _, iface, _) => found |= iface == "zwlr_layer_shell_v1",
            });
            display.Roundtrip();
            return found;
        }
        catch
        {
            return false;
        }
    }

    public void RequestShow() => _showRequested = true;

    public void PumpEvents()
    {
        _display.DispatchPending();
        _display.Flush();
        if (_display.PrepareRead() == 0)
        {
            if (LinuxInterop.PollReadable(_display.GetFd()))
            {
                _display.ReadEvents();
            }
            else
            {
                _display.CancelRead();
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

        if (_eglWindow == IntPtr.Zero ||
            (_logicalWidth == _appliedWidth && _logicalHeight == _appliedHeight && _scale == _appliedScale))
        {
            return;
        }

        _appliedWidth = _logicalWidth;
        _appliedHeight = _logicalHeight;
        _appliedScale = _scale;
        _surface?.SetBufferScale(_appliedScale);
        LinuxInterop.wl_egl_window_resize(
            _eglWindow, _appliedWidth * _appliedScale, _appliedHeight * _appliedScale, 0, 0);
    }

    public void Dispose()
    {
        _pointer?.Dispose();
        _pointer = null;
        _toplevel?.Dispose();
        _toplevel = null;
        _xdgSurface?.Dispose();
        _xdgSurface = null;
        _layerSurface?.Dispose();
        _layerSurface = null;
        if (_eglWindow != IntPtr.Zero)
        {
            LinuxInterop.wl_egl_window_destroy(_eglWindow);
            _eglWindow = IntPtr.Zero;
        }

        _surface?.Dispose();
        _surface = null;
        _seat?.Dispose();
        _seat = null;
        _output?.Dispose();
        _output = null;
        _wmBase?.Dispose();
        _wmBase = null;
        _layerShell?.Dispose();
        _layerShell = null;
        _compositor?.Dispose();
        _compositor = null;
        _registry?.Dispose();
        _display.Flush();
        _display.Dispose();
    }

    private void OnGlobal(WlRegistry registry, uint name, string iface, uint version)
    {
        switch (iface)
        {
            case "wl_compositor":
                _compositor = WlCompositor.Bind(registry, name, Math.Min(version, 4u));
                break;

            case "xdg_wm_base":
                _wmBase = XdgWmBase.Bind(registry, name, Math.Min(version, 2u), new XdgWmBase.Listener.Relay
                {
                    OnPing = (sender, serial) => sender.Pong(serial),
                });
                break;

            case "zwlr_layer_shell_v1":
                _layerShell = ZwlrLayerShellV1.Bind(registry, name, Math.Min(version, 4u));
                break;

            case "wl_seat" when _mode == DisplayMode.Window && _seat is null:
                _seat = WlSeat.Bind(registry, name, Math.Min(version, 5u), new WlSeat.Listener.Relay
                {
                    OnCapabilities = OnSeatCapabilities,
                });
                break;

            case "wl_output" when _output is null:
                _output = WlOutput.Bind(registry, name, Math.Min(version, 3u), new WlOutput.Listener.Relay
                {
                    OnMode = (_, flags, _, _, refresh) =>
                    {
                        if ((flags & WlOutput.ModeEnum.Current) != 0 && refresh > 0)
                        {
                            _refreshRate = refresh / 1000.0;
                        }
                    },
                    OnScale = (_, factor) =>
                    {
                        if (factor > 0)
                        {
                            _scale = factor;
                        }
                    },
                });
                break;
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
        _xdgSurface = _wmBase.GetXdgSurface(_surface, new XdgSurface.Listener.Relay
        {
            OnConfigure = (sender, serial) =>
            {
                sender.AckConfigure(serial);
                _configured = true;
            },
        });
        _toplevel = _xdgSurface.GetToplevel(new XdgToplevel.Listener.Relay
        {
            OnConfigure = (_, width, height, states) =>
            {
                _maximized = HasState(states, XdgToplevel.StateEnum.Maximized);
                if (width > 0 && height > 0)
                {
                    _pendingWidth = width;
                    _pendingHeight = height;
                }
            },
            OnClose = _ => Unmap(),
        });
        _toplevel.SetTitle("FreqScene");
        _toplevel.SetAppId("FreqScene");
        _toplevel.SetMinSize(320, 240);
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
            ? ZwlrLayerShellV1.LayerEnum.Top
            : ZwlrLayerShellV1.LayerEnum.Background;
        _layerSurface = _layerShell.GetLayerSurface(
            _surface, _output, layer, "freqscene", new ZwlrLayerSurfaceV1.Listener.Relay
            {
                OnConfigure = (sender, serial, width, height) =>
                {
                    sender.AckConfigure(serial);
                    if (width > 0 && height > 0)
                    {
                        _pendingWidth = (int)width;
                        _pendingHeight = (int)height;
                        if (!_configured)
                        {
                            _logicalWidth = (int)width;
                            _logicalHeight = (int)height;
                            _pendingWidth = 0;
                            _pendingHeight = 0;
                        }
                    }

                    _configured = true;
                },
                OnClosed = _ => _closedByCompositor = true,
            });
        _layerSurface.SetAnchor(
            ZwlrLayerSurfaceV1.AnchorEnum.Top | ZwlrLayerSurfaceV1.AnchorEnum.Bottom |
            ZwlrLayerSurfaceV1.AnchorEnum.Left | ZwlrLayerSurfaceV1.AnchorEnum.Right);
        _layerSurface.SetExclusiveZone(-1);
        _layerSurface.SetKeyboardInteractivity(ZwlrLayerSurfaceV1.KeyboardInteractivityEnum.None);

        if (mode == DisplayMode.Overlay)
        {
            // An empty input region makes the overlay click-through.
            using var region = _compositor.CreateRegion();
            _surface.SetInputRegion(region);
        }

        _surface.Commit();
    }

    private void OnSeatCapabilities(WlSeat seat, WlSeat.CapabilityEnum capabilities)
    {
        if ((capabilities & WlSeat.CapabilityEnum.Pointer) != 0 && _pointer is null)
        {
            _pointer = seat.GetPointer(new WlPointer.Listener.Relay
            {
                OnEnter = (_, _, _, x, y) =>
                {
                    _pointerX = (double)x;
                    _pointerY = (double)y;
                },
                OnMotion = (_, _, x, y) =>
                {
                    _pointerX = (double)x;
                    _pointerY = (double)y;
                },
                OnButton = OnPointerButton,
            });
        }
        else if ((capabilities & WlSeat.CapabilityEnum.Pointer) == 0 && _pointer is not null)
        {
            _pointer.Dispose();
            _pointer = null;
        }
    }

    private void OnPointerButton(WlPointer pointer, uint serial, uint time, uint button, WlPointer.ButtonStateEnum state)
    {
        if (button != BtnLeft || state != WlPointer.ButtonStateEnum.Pressed ||
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

        var edge = _maximized ? XdgToplevel.ResizeEdgeEnum.None : HitTestResizeEdge();
        if (edge != XdgToplevel.ResizeEdgeEnum.None)
        {
            _toplevel.Resize(_seat, serial, edge);
        }
        else
        {
            _toplevel.Move(_seat, serial);
        }
    }

    private XdgToplevel.ResizeEdgeEnum HitTestResizeEdge()
    {
        var edge = XdgToplevel.ResizeEdgeEnum.None;
        if (_pointerY < ResizeMargin)
        {
            edge |= XdgToplevel.ResizeEdgeEnum.Top;
        }
        else if (_pointerY >= _logicalHeight - ResizeMargin)
        {
            edge |= XdgToplevel.ResizeEdgeEnum.Bottom;
        }

        if (_pointerX < ResizeMargin)
        {
            edge |= XdgToplevel.ResizeEdgeEnum.Left;
        }
        else if (_pointerX >= _logicalWidth - ResizeMargin)
        {
            edge |= XdgToplevel.ResizeEdgeEnum.Right;
        }

        return edge;
    }

    private static bool HasState(ReadOnlySpan<byte> states, XdgToplevel.StateEnum state)
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
