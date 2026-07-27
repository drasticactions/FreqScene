using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FreqScene;

[SupportedOSPlatform("windows10.0.14393")]
internal sealed unsafe class WindowsVisualizerWindow : INativeVisualizerWindow
{
    private const string WindowClassName = "FreqSceneVisualizerWindow";

    private static bool s_classRegistered;

    private readonly VisualizerCoordinator _coordinator;
    private readonly WindowsVisualizerHost _host;
    private readonly Action<int> _onRenderScaleChanged;
    private HWND _hwnd;
    private bool _closed;

    public WindowsVisualizerWindow(
        VisualizerCoordinator coordinator, DisplayMode mode, string? displayKey, ILoggerFactory loggerFactory)
    {
        var log = loggerFactory.CreateLogger<WindowsVisualizerWindow>();
        EnsureClass();
        _coordinator = coordinator;

        const int width = 640;
        const int height = 640;
        var x = Math.Max(0, (PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN) - width) / 2);
        var y = Math.Max(0, (PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN) - height) / 2);

        _hwnd = PInvoke.CreateWindowEx(
            0, WindowClassName, "FreqScene", WINDOW_STYLE.WS_OVERLAPPEDWINDOW, x, y, width, height,
            HWND.Null, null, PInvoke.GetModuleHandle((string?)null), null);
        if (_hwnd.IsNull)
        {
            throw new InvalidOperationException(
                $"The visualizer window could not be created (error {Marshal.GetLastPInvokeError()}).");
        }

        _host = new WindowsVisualizerHost(_hwnd, transparent: false, loggerFactory)
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

        PInvoke.ShowWindow(_hwnd, SHOW_WINDOW_CMD.SW_SHOW);
        PInvoke.SetForegroundWindow(_hwnd);
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
        PInvoke.DestroyWindow(_hwnd);
        _hwnd = HWND.Null;
    }

    private static void EnsureClass()
    {
        if (s_classRegistered)
        {
            return;
        }

        // Pull the app icon embedded in the executable (ApplicationIcon) so the
        // taskbar and title bar show the FreqScene logo instead of the generic one.
        var largeIcon = HICON.Null;
        var smallIcon = HICON.Null;
        if (Environment.ProcessPath is { } exePath)
        {
            fixed (char* path = exePath)
            {
                PInvoke.ExtractIconEx(path, 0, &largeIcon, &smallIcon, 1);
            }
        }

        var wndClass = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            style = WNDCLASS_STYLES.CS_OWNDC | WNDCLASS_STYLES.CS_HREDRAW | WNDCLASS_STYLES.CS_VREDRAW,
            lpfnWndProc = &WndProc,
            hInstance = PInvoke.GetModuleHandle(default(PCWSTR)),
            hCursor = PInvoke.LoadCursor(default(HINSTANCE), PInvoke.IDC_ARROW),
            hIcon = largeIcon,
            hIconSm = smallIcon,
        };
        fixed (char* className = WindowClassName)
        {
            wndClass.lpszClassName = className;
            if (PInvoke.RegisterClassEx(in wndClass) == 0)
            {
                throw new InvalidOperationException(
                    $"The visualizer window class could not be registered (error {Marshal.GetLastPInvokeError()}).");
            }
        }

        s_classRegistered = true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_NCCALCSIZE when IsBorderless(hwnd):
                if (PInvoke.IsZoomed(hwnd))
                {
                    var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                    var info = default(MONITORINFOEXW);
                    info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
                    if (!monitor.IsNull && PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
                    {
                        *(RECT*)lParam.Value = info.monitorInfo.rcWork;
                    }
                }

                return default;

            case PInvoke.WM_NCHITTEST when IsBorderless(hwnd):
                return (LRESULT)(nint)HitTest(hwnd, lParam);

            case PInvoke.WM_ERASEBKGND:
                return (LRESULT)1;

            case PInvoke.WM_CLOSE:
                PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_HIDE);
                return default;

            case PInvoke.WM_DPICHANGED:
            {
                var suggested = (RECT*)lParam.Value;
                PInvoke.SetWindowPos(
                    hwnd, HWND.Null,
                    suggested->left, suggested->top, suggested->Width, suggested->Height,
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
                return default;
            }
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static bool IsBorderless(HWND hwnd) =>
        (PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & (int)WINDOW_STYLE.WS_THICKFRAME) != 0;

    private static uint HitTest(HWND hwnd, LPARAM lParam)
    {
        if (!PInvoke.GetWindowRect(hwnd, out var rect))
        {
            return PInvoke.HTCLIENT;
        }

        var packed = (long)lParam.Value;
        int x = (short)(packed & 0xFFFF);
        int y = (short)((packed >> 16) & 0xFFFF);

        if (!PInvoke.IsZoomed(hwnd))
        {
            var margin = Math.Max(4, (int)(8 * PInvoke.GetDpiForWindow(hwnd) / 96.0));
            var left = x < rect.left + margin;
            var right = x >= rect.right - margin;
            var top = y < rect.top + margin;
            var bottom = y >= rect.bottom - margin;
            if (top)
            {
                return left ? PInvoke.HTTOPLEFT : right ? PInvoke.HTTOPRIGHT : PInvoke.HTTOP;
            }

            if (bottom)
            {
                return left ? PInvoke.HTBOTTOMLEFT : right ? PInvoke.HTBOTTOMRIGHT : PInvoke.HTBOTTOM;
            }

            if (left)
            {
                return PInvoke.HTLEFT;
            }

            if (right)
            {
                return PInvoke.HTRIGHT;
            }
        }

        return PInvoke.HTCAPTION;
    }
}
