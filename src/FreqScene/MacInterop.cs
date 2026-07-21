using System.Runtime.InteropServices;

namespace FreqScene;

internal static partial class MacInterop
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.dylib";

    [StructLayout(LayoutKind.Sequential)]
    public struct CgPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CgSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CgRect
    {
        public CgPoint Origin;
        public CgSize Size;

        public CgRect(double x, double y, double width, double height)
        {
            Origin = new CgPoint { X = x, Y = y };
            Size = new CgSize { Width = width, Height = height };
        }
    }

    [LibraryImport(LibObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetClass(string name);

    [LibraryImport(LibObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr Sel(string name);

    [LibraryImport(LibObjC, EntryPoint = "objc_allocateClassPair", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr AllocateClassPair(IntPtr superclass, string name, nuint extraBytes);

    [LibraryImport(LibObjC, EntryPoint = "objc_registerClassPair")]
    public static partial void RegisterClassPair(IntPtr cls);

    [LibraryImport(LibObjC, EntryPoint = "class_addMethod", StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool AddMethod(IntPtr cls, IntPtr selector, IntPtr implementation, string typeEncoding);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void MsgSendVoid(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void MsgSendVoid(IntPtr receiver, IntPtr selector, IntPtr arg);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void MsgSendVoid(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void MsgSendVoid(IntPtr receiver, IntPtr selector, long value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void MsgSendVoid(IntPtr receiver, IntPtr selector, ulong value);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial long MsgSendLong(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial double MsgSendDouble(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool MsgSendBool(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    public static partial bool MsgSendBool(IntPtr receiver, IntPtr selector, IntPtr arg);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr MsgSendUtf8(IntPtr receiver, IntPtr selector, string arg);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void MsgSendVoid(IntPtr receiver, IntPtr selector, CgRect rect, [MarshalAs(UnmanagedType.I1)] bool flag);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSendInitWindow(
        IntPtr receiver, IntPtr selector, CgRect contentRect, ulong styleMask, ulong backing, [MarshalAs(UnmanagedType.I1)] bool defer);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial void MsgSendSetValues(IntPtr receiver, IntPtr selector, ref int value, long parameter);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSendInitAttributes(IntPtr receiver, IntPtr selector, uint[] attributes);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static partial CgRect MsgSendRectArm64(IntPtr receiver, IntPtr selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend_stret")]
    private static partial CgRect MsgSendRectX64(IntPtr receiver, IntPtr selector);

    public static CgRect MsgSendRect(IntPtr receiver, IntPtr selector) =>
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? MsgSendRectArm64(receiver, selector)
            : MsgSendRectX64(receiver, selector);

    [LibraryImport(LibObjC, EntryPoint = "objc_msgSend")]
    public static partial IntPtr MsgSendInitFrame(IntPtr receiver, IntPtr selector, CgRect frame);

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [LibraryImport(CoreGraphics, EntryPoint = "CGMainDisplayID")]
    public static partial uint MainDisplayId();

    [LibraryImport(CoreGraphics, EntryPoint = "CGDisplayBounds")]
    public static partial CgRect DisplayBounds(uint display);

    [LibraryImport(CoreGraphics, EntryPoint = "CGDisplayVendorNumber")]
    public static partial uint DisplayVendorNumber(uint display);

    [LibraryImport(CoreGraphics, EntryPoint = "CGDisplayModelNumber")]
    public static partial uint DisplayModelNumber(uint display);

    [LibraryImport(CoreGraphics, EntryPoint = "CGDisplaySerialNumber")]
    public static partial uint DisplaySerialNumber(uint display);

    [LibraryImport(LibSystem, EntryPoint = "dlopen", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr DlOpen(string path, int mode);

    [LibraryImport(LibSystem, EntryPoint = "dlsym", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr DlSym(IntPtr handle, string symbol);
}
