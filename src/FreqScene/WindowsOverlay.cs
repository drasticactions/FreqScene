using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace FreqScene;

internal static partial class WindowsOverlay
{
    private const string User32 = "user32.dll";

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x0000_0020;
    private const int WsExToolWindow = 0x0000_0080;
    private const int WsExLayered = 0x0008_0000;
    private const int WsExNoActivate = 0x0800_0000;

    private const uint LwaColorKey = 0x0000_0001;
    private const uint LwaAlpha = 0x0000_0002;

    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNoTopMost = new(-2);
    private static readonly IntPtr HwndBottom = new(1);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private const int SwShowNoActivate = 4;
    private const uint GaParent = 1;

    private const uint WmSpawnWorker = 0x052C;
    private const uint SmtoNormal = 0x0000;

    public static void ConfigureOverlay(Window window, bool wallpaper)
    {
        if (GetHwnd(window) is not { } hwnd)
        {
            return;
        }

        var exStyle = GetWindowLongPtrW(hwnd, GwlExStyle).ToInt64();
        exStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered;
        SetWindowLongPtrW(hwnd, GwlExStyle, new IntPtr(exStyle));

        SetStacking(window, wallpaper);
    }

    /// <summary>
    /// Keys pure black out of the window, so the desktop shows through the
    /// visualization's background.
    /// </summary>
    public static void SetBlackKeyedOut(Window window, bool keyed)
    {
        if (GetHwnd(window) is not { } hwnd)
        {
            return;
        }

        SetLayeredWindowAttributes(
            hwnd,
            colorKey: 0x00_00_00,
            alpha: 255,
            flags: keyed ? LwaColorKey : LwaAlpha);
    }

    /// <summary>Floats the window above all windows (false) or behind the desktop icons (true).</summary>
    public static void SetStacking(Window window, bool wallpaper)
    {
        if (GetHwnd(window) is not { } hwnd)
        {
            return;
        }

        if (wallpaper)
        {
            SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

            var host = FindWallpaperHost();
            SetParent(hwnd, host);
            if (host == IntPtr.Zero)
            {
                SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
            }
        }
        else
        {
            SetParent(hwnd, IntPtr.Zero);
            SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        ApplyBounds(window);

        // Leaving the wallpaper host leaves the window hidden.
        ShowWindow(hwnd, SwShowNoActivate);
    }

    public static void ApplyBounds(Window window)
    {
        if (GetHwnd(window) is not { } hwnd)
        {
            return;
        }

        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        if (screen is null)
        {
            return;
        }

        var bounds = screen.Bounds;
        var origin = new PixelPoint(0, 0);

        var host = GetAncestor(hwnd, GaParent);
        if (host != IntPtr.Zero && host != GetDesktopWindow() && GetWindowRect(host, out var hostRect))
        {
            origin = new PixelPoint(hostRect.Left, hostRect.Top);
        }

        SetWindowPos(
            hwnd,
            IntPtr.Zero,
            bounds.X - origin.X,
            bounds.Y - origin.Y,
            bounds.Width,
            bounds.Height,
            SwpNoActivate | SwpShowWindow);
    }

    public static void RefreshDesktop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var progman = FindWindowW("Progman", null);
        if (progman != IntPtr.Zero)
        {
            SendMessageTimeoutW(progman, WmSpawnWorker, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _);
        }
    }

    private static IntPtr FindWallpaperHost()
    {
        var progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        SendMessageTimeoutW(progman, WmSpawnWorker, IntPtr.Zero, IntPtr.Zero, SmtoNormal, 1000, out _);

        var candidate = IntPtr.Zero;
        while ((candidate = FindWindowExW(IntPtr.Zero, candidate, "WorkerW", null)) != IntPtr.Zero)
        {
            if (FindWindowExW(candidate, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
            {
                var worker = FindWindowExW(IntPtr.Zero, candidate, "WorkerW", null);
                if (worker != IntPtr.Zero)
                {
                    return worker;
                }
            }
        }

        return FindWindowExW(progman, IntPtr.Zero, "WorkerW", null);
    }

    private static IntPtr? GetHwnd(Window window)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        return handle == IntPtr.Zero ? null : handle;
    }

    [LibraryImport(User32, EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);

    [LibraryImport(User32, EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static partial IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr value);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport(User32, SetLastError = true)]
    private static partial IntPtr SetParent(IntPtr child, IntPtr newParent);

    [LibraryImport(User32, EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr FindWindowW(string? className, string? windowName);

    [LibraryImport(User32, EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [LibraryImport(User32, EntryPoint = "SendMessageTimeoutW")]
    private static partial IntPtr SendMessageTimeoutW(
        IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [LibraryImport(User32)]
    private static partial IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [LibraryImport(User32)]
    private static partial IntPtr GetDesktopWindow();

    [LibraryImport(User32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport(User32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
