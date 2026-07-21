using System.Runtime.InteropServices;

namespace FreqScene;

internal sealed class MacVisualizerWindow : INativeVisualizerWindow
{
    private const ulong StyleMaskResizable = 1UL << 3;
    private const ulong BackingStoreBuffered = 2;

    private static readonly IntPtr SelAlloc = MacInterop.Sel("alloc");
    private static readonly IntPtr SelRelease = MacInterop.Sel("release");

    private static IntPtr s_viewClass;
    private static IntPtr s_keyWindowClass;

    private readonly VisualizerCoordinator _coordinator;
    private readonly MacVisualizerHost _host;
    private readonly Action<int> _onRenderScaleChanged;
    private readonly DisplayMode _mode;
    private IntPtr _window;
    private bool _closed;

    public MacVisualizerWindow(VisualizerCoordinator coordinator, DisplayMode mode)
    {
        EnsureClasses();
        _coordinator = coordinator;
        _mode = mode;

        var isWindowMode = mode == DisplayMode.Window;
        MacInterop.CgRect contentRect;
        IntPtr windowClass;
        ulong styleMask;
        if (isWindowMode)
        {
            contentRect = new MacInterop.CgRect(0, 0, 640, 640);
            windowClass = s_keyWindowClass;
            styleMask = StyleMaskResizable; // borderless + edge resizing
        }
        else
        {
            var bounds = MacInterop.DisplayBounds(MacInterop.MainDisplayId());
            contentRect = new MacInterop.CgRect(0, 0, bounds.Size.Width, bounds.Size.Height);
            windowClass = MacInterop.GetClass("NSWindow");
            styleMask = 0;
        }

        _window = MacInterop.MsgSendInitWindow(
            MacInterop.MsgSend(windowClass, SelAlloc),
            MacInterop.Sel("initWithContentRect:styleMask:backing:defer:"),
            contentRect, styleMask, BackingStoreBuffered, false);
        MacInterop.MsgSendVoid(_window, MacInterop.Sel("setReleasedWhenClosed:"), false);

        if (isWindowMode)
        {
            MacInterop.MsgSendVoid(_window, MacInterop.Sel("center"));
        }
        else
        {
            var clearColor = MacInterop.MsgSend(MacInterop.GetClass("NSColor"), MacInterop.Sel("clearColor"));
            MacInterop.MsgSendVoid(_window, MacInterop.Sel("setBackgroundColor:"), clearColor);
            MacOverlay.ConfigureOverlay(_window, wallpaper: mode == DisplayMode.Wallpaper);
        }

        var view = MacInterop.MsgSendInitFrame(
            MacInterop.MsgSend(s_viewClass, SelAlloc), MacInterop.Sel("initWithFrame:"),
            new MacInterop.CgRect(0, 0, contentRect.Size.Width, contentRect.Size.Height));
        MacInterop.MsgSendVoid(view, MacInterop.Sel("setWantsBestResolutionOpenGLSurface:"), true);
        MacInterop.MsgSendVoid(_window, MacInterop.Sel("setContentView:"), view);
        MacInterop.MsgSendVoid(view, SelRelease); // the window retains it

        var transparent = mode == DisplayMode.Overlay ||
            (mode == DisplayMode.Wallpaper && coordinator.WallpaperTransparency);
        _host = new MacVisualizerHost(_window, view, transparent: transparent)
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

        if (_mode == DisplayMode.Window)
        {
            var app = MacInterop.MsgSend(MacInterop.GetClass("NSApplication"), MacInterop.Sel("sharedApplication"));
            MacInterop.MsgSendVoid(app, MacInterop.Sel("activateIgnoringOtherApps:"), true);
            MacInterop.MsgSendVoid(_window, MacInterop.Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
        }
        else
        {
            MacInterop.MsgSendVoid(_window, MacInterop.Sel("orderFrontRegardless"));
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
        MacInterop.MsgSendVoid(_window, MacInterop.Sel("close"));
        MacInterop.MsgSendVoid(_window, SelRelease);
        _window = IntPtr.Zero;
    }

    private static unsafe void EnsureClasses()
    {
        if (s_viewClass != IntPtr.Zero)
        {
            return;
        }

        var viewClass = MacInterop.AllocateClassPair(MacInterop.GetClass("NSView"), "FreqSceneVisualizerView", 0);
        MacInterop.AddMethod(
            viewClass, MacInterop.Sel("mouseDown:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnMouseDown, "v@:@");
        MacInterop.RegisterClassPair(viewClass);
        s_viewClass = viewClass;

        // Borderless windows refuse key/main status by default; the visualizer
        // window needs both to receive clicks and behave like a normal window.
        var windowClass = MacInterop.AllocateClassPair(MacInterop.GetClass("NSWindow"), "FreqSceneVisualizerWindow", 0);
        MacInterop.AddMethod(
            windowClass, MacInterop.Sel("canBecomeKeyWindow"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&ReturnTrue, "c@:");
        MacInterop.AddMethod(
            windowClass, MacInterop.Sel("canBecomeMainWindow"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, byte>)&ReturnTrue, "c@:");
        MacInterop.RegisterClassPair(windowClass);
        s_keyWindowClass = windowClass;
    }

    [UnmanagedCallersOnly]
    private static void OnMouseDown(IntPtr self, IntPtr selector, IntPtr theEvent)
    {
        var window = MacInterop.MsgSend(self, MacInterop.Sel("window"));
        if (window == IntPtr.Zero)
        {
            return;
        }

        // No title bar: double-click toggles zoom, any other press drags the window.
        if (MacInterop.MsgSendLong(theEvent, MacInterop.Sel("clickCount")) >= 2)
        {
            MacInterop.MsgSendVoid(window, MacInterop.Sel("zoom:"), IntPtr.Zero);
        }
        else
        {
            MacInterop.MsgSendVoid(window, MacInterop.Sel("performWindowDragWithEvent:"), theEvent);
        }
    }

    [UnmanagedCallersOnly]
    private static byte ReturnTrue(IntPtr self, IntPtr selector) => 1;
}
