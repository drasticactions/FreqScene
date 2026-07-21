using System.Runtime.InteropServices;

namespace FreqScene;

internal static unsafe partial class WindowsInterop
{
    private const string User32 = "user32.dll";
    private const string Gdi32 = "gdi32.dll";
    private const string OpenGl32 = "opengl32.dll";
    private const string DwmApi = "dwmapi.dll";
    private const string WinMm = "winmm.dll";
    private const string Kernel32 = "kernel32.dll";
    private const string Ole32 = "ole32.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WndClassExW
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClassExtra;
        public int WindowExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public IntPtr MenuName;
        public IntPtr ClassName;
        public IntPtr IconSmall;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PixelFormatDescriptor
    {
        public ushort Size;
        public ushort Version;
        public uint Flags;
        public byte PixelType;
        public byte ColorBits;
        public byte RedBits;
        public byte RedShift;
        public byte GreenBits;
        public byte GreenShift;
        public byte BlueBits;
        public byte BlueShift;
        public byte AlphaBits;
        public byte AlphaShift;
        public byte AccumBits;
        public byte AccumRedBits;
        public byte AccumGreenBits;
        public byte AccumBlueBits;
        public byte AccumAlphaBits;
        public byte DepthBits;
        public byte StencilBits;
        public byte AuxBuffers;
        public byte LayerType;
        public byte Reserved;
        public uint LayerMask;
        public uint VisibleMask;
        public uint DamageMask;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MonitorInfoExW
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        public fixed char Device[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DevModeW
    {
        public fixed char DeviceName[32];
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TtOption;
        public short Collate;
        public fixed char FormName[32];
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint IcmMethod;
        public uint IcmIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [LibraryImport(Kernel32, EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr GetModuleHandleW(string? moduleName);

    [LibraryImport(Kernel32, EntryPoint = "GetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetProcAddress(IntPtr module, string name);

    [LibraryImport(User32, EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassExW(ref WndClassExW windowClass);

    [LibraryImport(User32, EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [LibraryImport(User32, EntryPoint = "DefWindowProcW")]
    public static partial IntPtr DefWindowProcW(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hwnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(IntPtr hwnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hwnd, out Rect rect);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [LibraryImport(User32)]
    public static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport(User32)]
    public static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [LibraryImport(User32)]
    public static partial int GetSystemMetrics(int index);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    public static partial IntPtr SetParent(IntPtr child, IntPtr newParent);

    [LibraryImport(User32, EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowW(string? className, string? windowName);

    [LibraryImport(User32, EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [LibraryImport(User32, EntryPoint = "SendMessageTimeoutW")]
    public static partial IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [LibraryImport(User32)]
    public static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport(User32, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoExW info);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(
        IntPtr dc, IntPtr clip, delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, int> callback, IntPtr data);

    [LibraryImport(User32, EntryPoint = "LoadCursorW")]
    public static partial IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [LibraryImport(User32)]
    public static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsZoomed(IntPtr hwnd);

    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW")]
    public static partial IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport(User32, EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplaySettingsW(string deviceName, uint modeNum, ref DevModeW devMode);

    [LibraryImport(Gdi32, SetLastError = true)]
    public static partial int ChoosePixelFormat(IntPtr dc, ref PixelFormatDescriptor descriptor);

    [LibraryImport(Gdi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetPixelFormat(IntPtr dc, int format, ref PixelFormatDescriptor descriptor);

    [LibraryImport(Gdi32)]
    public static partial int DescribePixelFormat(IntPtr dc, int format, uint size, ref PixelFormatDescriptor descriptor);

    [LibraryImport(Gdi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SwapBuffers(IntPtr dc);

    [LibraryImport(OpenGl32, EntryPoint = "wglCreateContext", SetLastError = true)]
    public static partial IntPtr WglCreateContext(IntPtr dc);

    [LibraryImport(OpenGl32, EntryPoint = "wglDeleteContext")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WglDeleteContext(IntPtr context);

    [LibraryImport(OpenGl32, EntryPoint = "wglMakeCurrent", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WglMakeCurrent(IntPtr dc, IntPtr context);

    [LibraryImport(OpenGl32, EntryPoint = "wglGetProcAddress", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr WglGetProcAddress(string name);

    [LibraryImport(DwmApi, EntryPoint = "DwmExtendFrameIntoClientArea")]
    public static partial int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport(Gdi32)]
    public static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport(Gdi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(IntPtr dc);

    [LibraryImport(Gdi32)]
    public static partial IntPtr CreateDIBSection(
        IntPtr dc, in BitmapInfoHeader info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport(Gdi32)]
    public static partial IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [LibraryImport(Gdi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(IntPtr gdiObject);

    [LibraryImport(Gdi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();

    [LibraryImport(User32, EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfoW(uint action, uint uiParam, char* pvParam, uint winIni);

    [LibraryImport(Ole32)]
    public static partial int CoInitializeEx(IntPtr reserved, uint apartment);

    [LibraryImport(Ole32)]
    public static partial void CoUninitialize();

    [LibraryImport(Ole32)]
    public static partial int CoCreateInstance(
        in Guid clsid, IntPtr outer, uint context, in Guid iid, out IntPtr instance);

    [LibraryImport(Ole32)]
    public static partial void CoTaskMemFree(IntPtr pointer);

    [LibraryImport(WinMm, EntryPoint = "timeBeginPeriod")]
    public static partial uint TimeBeginPeriod(uint period);

    [LibraryImport(WinMm, EntryPoint = "timeEndPeriod")]
    public static partial uint TimeEndPeriod(uint period);
}
