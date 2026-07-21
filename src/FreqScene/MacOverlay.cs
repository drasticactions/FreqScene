using System.Runtime.InteropServices;

namespace FreqScene;

internal static partial class MacOverlay
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    // CGWindowLevelKey values (CGWindowLevel.h).
    private const int DesktopWindowLevelKey = 2;
    private const int FloatingWindowLevelKey = 5;

    private const ulong CollectionBehavior = (1UL << 0) | (1UL << 4) | (1UL << 6);

    public static void ConfigureOverlay(IntPtr nsWindow, bool wallpaper)
    {
        MsgSendBool(nsWindow, SelRegisterName("setIgnoresMouseEvents:"), true);
        MsgSendBool(nsWindow, SelRegisterName("setOpaque:"), false);
        MsgSendBool(nsWindow, SelRegisterName("setHasShadow:"), false);
        MsgSendULong(nsWindow, SelRegisterName("setCollectionBehavior:"), CollectionBehavior);
        SetLevel(nsWindow, wallpaper);
    }

    public static void SetLevel(IntPtr nsWindow, bool wallpaper)
    {
        var level = wallpaper
            ? WindowLevelForKey(DesktopWindowLevelKey) + 1
            : WindowLevelForKey(FloatingWindowLevelKey);
        MsgSendLong(nsWindow, SelRegisterName("setLevel:"), level);
    }

    [LibraryImport(LibObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr SelRegisterName(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void MsgSendBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void MsgSendLong(IntPtr receiver, IntPtr selector, long value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial void MsgSendULong(IntPtr receiver, IntPtr selector, ulong value);

    [LibraryImport(CoreGraphics, EntryPoint = "CGWindowLevelForKey")]
    private static partial int WindowLevelForKey(int key);
}
