using System.Runtime.InteropServices;
using Avalonia.Controls;

namespace FreqScene;

internal static partial class MacOverlay
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // CGWindowLevelKey values (CGWindowLevel.h).
    private const int DesktopWindowLevelKey = 2;
    private const int FloatingWindowLevelKey = 5;

    private const ulong CollectionBehavior = (1UL << 0) | (1UL << 4) | (1UL << 6);

    public static void ConfigureOverlay(Window window, bool wallpaper)
    {
        if (GetNsWindow(window) is not { } nsWindow)
        {
            return;
        }

        MsgSendBool(nsWindow, SelRegisterName("setIgnoresMouseEvents:"), true);
        MsgSendBool(nsWindow, SelRegisterName("setOpaque:"), false);
        MsgSendBool(nsWindow, SelRegisterName("setHasShadow:"), false);
        MsgSendULong(nsWindow, SelRegisterName("setCollectionBehavior:"), CollectionBehavior);
        SetLevel(window, wallpaper);
    }

    public static void SetLevel(Window window, bool wallpaper)
    {
        if (GetNsWindow(window) is not { } nsWindow)
        {
            return;
        }

        var level = wallpaper
            ? WindowLevelForKey(DesktopWindowLevelKey) + 1
            : WindowLevelForKey(FloatingWindowLevelKey);
        MsgSendLong(nsWindow, SelRegisterName("setLevel:"), level);
    }

    public static void SetFullScreenFrame(Window window)
    {
        if (GetNsWindow(window) is not { } nsWindow)
        {
            return;
        }

        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var rect = new CgRect
        {
            X = 0,
            Y = 0,
            Width = screen.Bounds.Width / screen.Scaling,
            Height = screen.Bounds.Height / screen.Scaling,
        };
        MsgSendRectBool(nsWindow, SelRegisterName("setFrame:display:"), rect, true);
    }

    private static IntPtr? GetNsWindow(Window window)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle == IntPtr.Zero ? null : handle;
    }

    [LibraryImport(LibObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr SelRegisterName(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void MsgSendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void MsgSendLong(IntPtr receiver, IntPtr selector, long value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void MsgSendULong(IntPtr receiver, IntPtr selector, ulong value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void MsgSendRectBool(IntPtr receiver, IntPtr selector, CgRect rect, [MarshalAs(UnmanagedType.I1)] bool display);

    [StructLayout(LayoutKind.Sequential)]
    private struct CgRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    [LibraryImport(CoreGraphics, EntryPoint = "CGWindowLevelForKey")]
    private static partial int WindowLevelForKey(int key);
}
