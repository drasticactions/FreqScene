using System.Runtime.InteropServices;

namespace FreqScene;

internal sealed unsafe class WindowsVisualizerWindow : INativeVisualizerWindow
{
    private const string WindowClassName = "FreqSceneVisualizerWindow";

    private const uint ClassStyleOwnDc = 0x0020;
    private const uint ClassStyleHRedraw = 0x0002;
    private const uint ClassStyleVRedraw = 0x0001;

    private const uint WsOverlappedWindow = 0x00CF_0000;
    private const uint WsThickFrame = 0x0004_0000;

    private const uint WmClose = 0x0010;
    private const uint WmEraseBackground = 0x0014;
    private const uint WmNcCalcSize = 0x0083;
    private const uint WmNcHitTest = 0x0084;
    private const uint WmDpiChanged = 0x02E0;

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
    private const int SwShow = 5;

    private const int GwlStyle = -16;
    private const uint MonitorDefaultToNearest = 2;

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private const int IdcArrow = 32512;

    private static bool s_classRegistered;

    private readonly VisualizerCoordinator _coordinator;
    private readonly WindowsVisualizerHost _host;
    private readonly Action<int> _onRenderScaleChanged;
    private IntPtr _hwnd;
    private bool _closed;

    public WindowsVisualizerWindow(VisualizerCoordinator coordinator, DisplayMode mode, string? displayKey)
    {
        EnsureClass();
        _coordinator = coordinator;

        const int width = 640;
        const int height = 640;
        var x = Math.Max(0, (WindowsInterop.GetSystemMetrics(SmCxScreen) - width) / 2);
        var y = Math.Max(0, (WindowsInterop.GetSystemMetrics(SmCyScreen) - height) / 2);

        _hwnd = WindowsInterop.CreateWindowExW(
            0, WindowClassName, "FreqScene", WsOverlappedWindow, x, y, width, height,
            IntPtr.Zero, IntPtr.Zero, WindowsInterop.GetModuleHandleW(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"The visualizer window could not be created (error {Marshal.GetLastPInvokeError()}).");
        }

        _host = new WindowsVisualizerHost(_hwnd, transparent: false)
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

        WindowsInterop.ShowWindow(_hwnd, SwShow);
        WindowsInterop.SetForegroundWindow(_hwnd);
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
        WindowsInterop.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
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
