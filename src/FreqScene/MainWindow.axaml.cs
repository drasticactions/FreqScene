using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

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

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Activated += OnActivated;
        }
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