using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

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

        ScaleHost.RenderScale = coordinator.RenderScalePercent / 100.0;
        Action<int> onRenderScaleChanged = percent => ScaleHost.RenderScale = percent / 100.0;
        coordinator.RenderScaleChanged += onRenderScaleChanged;
        Closed += (_, _) => coordinator.RenderScaleChanged -= onRenderScaleChanged;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Activated += OnActivated;
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (OperatingSystem.IsWindowsVersionAtLeast(10))
        {
            if (TryGetPlatformHandle() is { } handle)
            {
                unsafe
                {
                    var cornerPreference = DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
                    PInvoke.DwmSetWindowAttribute(
                        (HWND)handle.Handle, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE,
                        &cornerPreference, (uint)sizeof(DWM_WINDOW_CORNER_PREFERENCE));
                }
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (e.Handled)
        {
            return;
        }

        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        // No title bar to grab, so the whole window surface moves it.
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
        e.Handled = true;
    }

    private void OnActivated(object? sender, EventArgs e)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && CanResize)
        {
            DispatcherTimer.RunOnce(() =>
            {
                if (CanResize && TryGetPlatformHandle() is { } handle)
                {
                    NativeMacOs.EnableResizable(handle.Handle);
                }
            }, TimeSpan.FromMilliseconds(1));
        }
    }

    private static class NativeMacOs
    {
        private const string ObjCLibrary = "/usr/lib/libobjc.dylib";
        [DllImport(ObjCLibrary, EntryPoint = "objc_getClass")]
        private static extern IntPtr objc_getClass(string className);

        [DllImport(ObjCLibrary, EntryPoint = "sel_registerName")]
        private static extern IntPtr sel_registerName(string selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

        [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
        private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector, ulong arg);

        public static void EnableResizable(IntPtr windowPtr)
        {
            IntPtr styleMaskSel = sel_registerName("styleMask");
            IntPtr styleMask = objc_msgSend(windowPtr, styleMaskSel);

            const ulong nsWindowStyleMaskResizable = 1 << 3; // Correct mask for resizable windows
            ulong currentMask = (ulong)styleMask.ToInt64();

            currentMask |= nsWindowStyleMaskResizable;

            IntPtr setStyleMaskSel = sel_registerName("setStyleMask:");
            objc_msgSend_void(windowPtr, setStyleMaskSel, currentMask);
        }
    }
}