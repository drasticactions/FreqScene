using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace FreqScene;

internal sealed unsafe class WindowsVisualizerWindow : INativeVisualizerWindow
{
    private const string WindowClassName = "FreqSceneVisualizerWindow";

    private const uint ClassStyleOwnDc = 0x0020;
    private const uint ClassStyleHRedraw = 0x0002;
    private const uint ClassStyleVRedraw = 0x0001;

    private const uint WsOverlappedWindow = 0x00CF_0000;
    private const uint WsPopup = 0x8000_0000;
    private const uint WsThickFrame = 0x0004_0000;

    private const uint WsExToolWindow = 0x0000_0080;
    private const uint WsExTopMost = 0x0000_0008;
    private const uint WsExTransparent = 0x0000_0020;
    private const uint WsExLayered = 0x0008_0000;
    private const uint WsExNoActivate = 0x0800_0000;

    private const uint WmClose = 0x0010;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmWindowPosChanging = 0x0046;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmDpiChanged = 0x02E0;
    private const uint WmSpawnWorker = 0x052C;

    private const int HitClient = 1;
    private const int HitCaption = 2;
    private const int HitLeft = 10;
    private const int HitRight = 11;
    private const int HitTop = 12;
    private const int HitTopLeft = 13;
    private const int HitTopRight = 14;
    private const int HitBottom = 15;
    private const int HitBottomLeft = 16;
    private const int HitBottomRight = 17;

    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int SwShow = 5;

    private const int GwlStyle = -16;
    private const uint LwaAlpha = 0x0000_0002;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SmtoNormal = 0x0000;

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private const int IdcArrow = 32512;

    private static readonly IntPtr HwndBottom = new(1);

    private static readonly HashSet<IntPtr> s_bottomPinned = [];

    private static bool s_classRegistered;

    private readonly VisualizerCoordinator _coordinator;
    private readonly WindowsVisualizerHost _host;
    private readonly Action<int> _onRenderScaleChanged;
    private readonly DisplayMode _mode;
    private readonly WindowsInterop.Rect _bounds;
    private IntPtr _hwnd;
    private bool _closed;

    public WindowsVisualizerWindow(VisualizerCoordinator coordinator, DisplayMode mode, string? displayKey)
    {
        EnsureClass();
        _coordinator = coordinator;
        _mode = mode;

        uint style;
        uint exStyle;
        int x, y, width, height;
        if (mode == DisplayMode.Window)
        {
            style = WsOverlappedWindow;
            exStyle = 0;
            width = 640;
            height = 640;
            x = Math.Max(0, (WindowsInterop.GetSystemMetrics(SmCxScreen) - width) / 2);
            y = Math.Max(0, (WindowsInterop.GetSystemMetrics(SmCyScreen) - height) / 2);
        }
        else
        {
            style = WsPopup;
            exStyle = mode == DisplayMode.Overlay
                ? WsExToolWindow | WsExNoActivate | WsExTopMost | WsExLayered | WsExTransparent
                : WsExToolWindow | WsExNoActivate;
            _bounds = WindowsDisplays.ResolveBounds(displayKey);
            x = _bounds.Left;
            y = _bounds.Top;
            width = _bounds.Right - _bounds.Left;
            height = _bounds.Bottom - _bounds.Top;
        }

        _hwnd = WindowsInterop.CreateWindowExW(
            exStyle, WindowClassName, "FreqScene", style, x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, WindowsInterop.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"The visualizer window could not be created (error {Marshal.GetLastPInvokeError()}).");
        }

        if (mode == DisplayMode.Overlay)
        {
            WindowsInterop.SetLayeredWindowAttributes(_hwnd, 0, 255, LwaAlpha);
            var margins = new WindowsInterop.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            WindowsInterop.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        }
        else if (mode == DisplayMode.Wallpaper)
        {
            AttachToWallpaperHost(x, y, width, height);
        }

        var wallpaperTransparent = mode == DisplayMode.Wallpaper && coordinator.WallpaperTransparency;
        _host = new WindowsVisualizerHost(_hwnd, transparent: mode == DisplayMode.Overlay || wallpaperTransparent)
        {
            RenderScale = coordinator.RenderScalePercent / 100.0,
        };
        _host.InitializationFailed += (_, ex) =>
            System.Diagnostics.Trace.TraceError($"[native] visualizer init failed: {ex}");
        _onRenderScaleChanged = percent => _host.RenderScale = percent / 100.0;
        coordinator.RenderScaleChanged += _onRenderScaleChanged;
        coordinator.AttachControl(_host);

        if (wallpaperTransparent)
        {
            CaptureWallpaperBackground();
        }
    }

    public void Show()
    {
        if (_closed)
        {
            return;
        }

        if (_mode == DisplayMode.Window)
        {
            WindowsInterop.ShowWindow(_hwnd, SwShow);
            WindowsInterop.SetForegroundWindow(_hwnd);
        }
        else
        {
            WindowsInterop.ShowWindow(_hwnd, SwShowNoActivate);
        }

        _host.Start();
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
        var pinned = s_bottomPinned.Remove(_hwnd);
        WindowsInterop.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;

        if (_mode == DisplayMode.Wallpaper && !pinned)
        {
            // Vacating the shell's wallpaper host leaves it blank otherwise.
            RefreshDesktop();
        }
    }

    private void CaptureWallpaperBackground()
    {
        if (WindowsInterop.GetClientRect(_hwnd, out var rect))
        {
            WallpaperBackground? captured = null;
            try
            {
                captured = WindowsWallpaper.CaptureShellRendering(
                    rect.Right - rect.Left, rect.Bottom - rect.Top, _hwnd);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"[native] wallpaper capture failed: {ex}");
            }

            if (captured is not null)
            {
                _host.SetWallpaperBackground(captured);
                return;
            }
        }

        var bounds = _bounds;
        Task.Run(() =>
        {
            try
            {
                var background = WindowsWallpaper.LoadBackground(
                    WindowsWallpaper.Query(bounds), bounds.Left, bounds.Top);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_closed)
                    {
                        _host.SetWallpaperBackground(background);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"[native] wallpaper query failed: {ex}");
            }
        });
    }

    private void AttachToWallpaperHost(int x, int y, int width, int height)
    {
        var host = FindWallpaperHost();
        if (host != IntPtr.Zero)
        {
            WindowsInterop.SetParent(_hwnd, host);
            var originX = 0;
            var originY = 0;
            if (WindowsInterop.GetWindowRect(host, out var hostRect))
            {
                originX = hostRect.Left;
                originY = hostRect.Top;
            }

            WindowsInterop.SetWindowPos(
                _hwnd, IntPtr.Zero, x - originX, y - originY, width, height, SwpNoZOrder | SwpNoActivate);
        }
        else
        {
            // No usable shell host: stay a top-level window pinned to the bottom of
            // the z-order (WndProc keeps it there). Desktop icons end up covered.
            s_bottomPinned.Add(_hwnd);
            WindowsInterop.SetWindowPos(_hwnd, HwndBottom, x, y, width, height, SwpNoActivate);
        }
    }

    private static IntPtr FindWallpaperHost()
    {
        var progman = WindowsInterop.FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // Since Windows 11 24H2 the desktop keeps SHELLDLL_DefView directly under
        // Progman, and builds 26120+ no longer composite foreign windows parented
        // anywhere into that tree — they render but never reach the screen.
        if (Environment.OSVersion.Version.Build >= 26120 &&
            WindowsInterop.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        WindowsInterop.SendMessageTimeoutW(progman, WmSpawnWorker, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _);

        var candidate = IntPtr.Zero;
        while ((candidate = WindowsInterop.FindWindowExW(IntPtr.Zero, candidate, "WorkerW", null)) != IntPtr.Zero)
        {
            if (WindowsInterop.FindWindowExW(candidate, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                var worker = WindowsInterop.FindWindowExW(IntPtr.Zero, candidate, "WorkerW", null);
                if (worker != IntPtr.Zero)
                {
                    return worker;
                }
            }
        }

        return WindowsInterop.FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
    }

    private static void RefreshDesktop()
    {
        var progman = WindowsInterop.FindWindowW("Progman", null);
        if (progman != IntPtr.Zero)
        {
            WindowsInterop.SendMessageTimeoutW(
                progman, WmSpawnWorker, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _);
        }
    }

    private static void EnsureClass()
    {
        if (s_classRegistered)
        {
            return;
        }

        var wndClass = new WindowsInterop.WndClassExW
        {
            Size = (uint)sizeof(WindowsInterop.WndClassExW),
            Style = ClassStyleOwnDc | ClassStyleHRedraw | ClassStyleVRedraw,
            WndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr>)&WndProc,
            Instance = WindowsInterop.GetModuleHandleW(null),
            Cursor = WindowsInterop.LoadCursorW(IntPtr.Zero, IdcArrow),
        };
        fixed (char* className = WindowClassName)
        {
            wndClass.ClassName = (IntPtr)className;
            if (WindowsInterop.RegisterClassExW(ref wndClass) == 0)
            {
                throw new InvalidOperationException(
                    $"The visualizer window class could not be registered (error {Marshal.GetLastPInvokeError()}).");
            }
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WmNcCalcSize when IsBorderless(hwnd):
                if (WindowsInterop.IsZoomed(hwnd))
                {
                    var monitor = WindowsInterop.MonitorFromWindow(hwnd, MonitorDefaultToNearest);
                    var info = new WindowsInterop.MonitorInfoExW
                    {
                        Size = (uint)sizeof(WindowsInterop.MonitorInfoExW),
                    };
                    if (monitor != IntPtr.Zero && WindowsInterop.GetMonitorInfoW(monitor, ref info))
                    {
                        *(WindowsInterop.Rect*)lParam = info.Work;
                    }
                }

                return IntPtr.Zero;

            case WmNcHitTest when IsBorderless(hwnd):
                return new IntPtr(HitTest(hwnd, lParam));

            case WmEraseBackground:
                return new IntPtr(1);

            case WmWindowPosChanging when s_bottomPinned.Contains(hwnd):
            {
                var pos = (WindowsInterop.WindowPos*)lParam;
                pos->InsertAfter = HwndBottom;
                pos->Flags &= ~SwpNoZOrder;
                return IntPtr.Zero;
            }

            case WmClose:
                WindowsInterop.ShowWindow(hwnd, SwHide);
                return IntPtr.Zero;

            case WmDpiChanged:
            {
                var suggested = (WindowsInterop.Rect*)lParam;
                WindowsInterop.SetWindowPos(
                    hwnd, IntPtr.Zero,
                    suggested->Left, suggested->Top,
                    suggested->Right - suggested->Left, suggested->Bottom - suggested->Top,
                    SwpNoZOrder | SwpNoActivate);
                return IntPtr.Zero;
            }
        }

        return WindowsInterop.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private static bool IsBorderless(IntPtr hwnd) =>
        (WindowsInterop.GetWindowLongPtrW(hwnd, GwlStyle).ToInt64() & WsThickFrame) != 0;

    private static int HitTest(IntPtr hwnd, IntPtr lParam)
    {
        if (!WindowsInterop.GetWindowRect(hwnd, out var rect))
        {
            return HitClient;
        }

        var packed = lParam.ToInt64();
        int x = (short)(packed & 0xFFFF);
        int y = (short)((packed >> 16) & 0xFFFF);

        if (!WindowsInterop.IsZoomed(hwnd))
        {
            var margin = Math.Max(4, (int)(8 * WindowsInterop.GetDpiForWindow(hwnd) / 96.0));
            var left = x < rect.Left + margin;
            var right = x >= rect.Right - margin;
            var top = y < rect.Top + margin;
            var bottom = y >= rect.Bottom - margin;
            if (top)
            {
                return left ? HitTopLeft : right ? HitTopRight : HitTop;
            }

            if (bottom)
            {
                return left ? HitBottomLeft : right ? HitBottomRight : HitBottom;
            }

            if (left)
            {
                return HitLeft;
            }

            if (right)
            {
                return HitRight;
            }
        }

        return HitCaption;
    }
}
