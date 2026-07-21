using System.Runtime.InteropServices;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace FreqScene;

/// <summary>A snapshot of the desktop wallpaper configuration.</summary>
internal sealed record WallpaperInfo(
    string? ImagePath,
    WallpaperPosition Position,
    uint BackgroundColor,
    int VirtualScreenX,
    int VirtualScreenY,
    int VirtualScreenWidth,
    int VirtualScreenHeight);

internal static unsafe class WindowsWallpaper
{
    private const uint ClsCtxAll = 0x17;
    private const uint CoInitApartmentThreaded = 0x2;
    private const uint SpiGetDeskWallpaper = 0x0073;
    private const int MaxPath = 260;

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;

    private const int SlotGetWallpaper = 4;
    private const int SlotGetMonitorDevicePathAt = 5;
    private const int SlotGetMonitorDevicePathCount = 6;
    private const int SlotGetMonitorRect = 7;
    private const int SlotGetBackgroundColor = 9;
    private const int SlotGetPosition = 11;

    private const uint PwRenderFullContent = 0x2;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private const int SwShowNa = 8;

    private static readonly Guid ClsidDesktopWallpaper = new("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD");
    private static readonly Guid IidIDesktopWallpaper = new("B92B56A9-8B55-4E14-9A89-0199BBB6F93B");

    public static WallpaperInfo Query(WindowsInterop.Rect? monitorBounds = null)
    {
        var (path, position, color) = QueryDesktopWallpaper(monitorBounds) ?? QuerySystemParameters();
        return new WallpaperInfo(
            path,
            position,
            color,
            WindowsInterop.GetSystemMetrics(SmXVirtualScreen),
            WindowsInterop.GetSystemMetrics(SmYVirtualScreen),
            WindowsInterop.GetSystemMetrics(SmCxVirtualScreen),
            WindowsInterop.GetSystemMetrics(SmCyVirtualScreen));
    }

    public static WallpaperBackground LoadBackground(WallpaperInfo info, int originX = 0, int originY = 0)
    {
        (byte[] Pixels, int Width, int Height)? image = null;
        if (info.ImagePath is { } path && File.Exists(path))
        {
            try
            {
                image = DecodeImage(path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError($"[native] wallpaper decode failed for '{path}': {ex}");
            }
        }

        return new WallpaperBackground
        {
            BgraPixels = image?.Pixels,
            ImageWidth = image?.Width ?? 0,
            ImageHeight = image?.Height ?? 0,
            Position = info.Position,
            BackgroundRed = (info.BackgroundColor & 0xFF) / 255f,
            BackgroundGreen = ((info.BackgroundColor >> 8) & 0xFF) / 255f,
            BackgroundBlue = ((info.BackgroundColor >> 16) & 0xFF) / 255f,
            SpanX = info.VirtualScreenX - originX,
            SpanY = info.VirtualScreenY - originY,
            SpanWidth = info.VirtualScreenWidth,
            SpanHeight = info.VirtualScreenHeight,
        };
    }

    public static WallpaperBackground? CaptureShellRendering(int width, int height, IntPtr excludeWindow)
    {
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var progman = WindowsInterop.FindWindowW("Progman", null);
        if (progman == IntPtr.Zero)
        {
            return null;
        }

        var target = progman;
        var iconLayer = WindowsInterop.FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (iconLayer == IntPtr.Zero)
        {
            var candidate = IntPtr.Zero;
            while ((candidate = WindowsInterop.FindWindowExW(IntPtr.Zero, candidate, "WorkerW", null)) != IntPtr.Zero)
            {
                if (WindowsInterop.FindWindowExW(candidate, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    var worker = WindowsInterop.FindWindowExW(IntPtr.Zero, candidate, "WorkerW", null);
                    if (worker != IntPtr.Zero)
                    {
                        target = worker;
                    }

                    break;
                }
            }
        }

        if (!WindowsInterop.GetWindowRect(target, out var targetRect))
        {
            return null;
        }

        var targetWidth = targetRect.Right - targetRect.Left;
        var targetHeight = targetRect.Bottom - targetRect.Top;

        var cropX = -targetRect.Left;
        var cropY = -targetRect.Top;
        if (excludeWindow != IntPtr.Zero && WindowsInterop.GetWindowRect(excludeWindow, out var windowRect))
        {
            cropX = windowRect.Left - targetRect.Left;
            cropY = windowRect.Top - targetRect.Top;
        }

        if (cropX < 0 || cropY < 0 || cropX + width > targetWidth || cropY + height > targetHeight)
        {
            return null;
        }

        var screenDc = WindowsInterop.GetDC(IntPtr.Zero);
        var memoryDc = WindowsInterop.CreateCompatibleDC(screenDc);
        var info = new WindowsInterop.BitmapInfoHeader
        {
            Size = (uint)sizeof(WindowsInterop.BitmapInfoHeader),
            Width = targetWidth,
            Height = -targetHeight, // top-down
            Planes = 1,
            BitCount = 32,
        };
        var dib = WindowsInterop.CreateDIBSection(memoryDc, in info, 0, out var bits, IntPtr.Zero, 0);
        if (dib == IntPtr.Zero || bits == IntPtr.Zero)
        {
            WindowsInterop.DeleteDC(memoryDc);
            WindowsInterop.ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        bool printed;
        var previous = WindowsInterop.SelectObject(memoryDc, dib);
        var hideExclude = excludeWindow != IntPtr.Zero && WindowsInterop.IsWindowVisible(excludeWindow);
        try
        {
            if (hideExclude)
            {
                WindowsInterop.ShowWindow(excludeWindow, SwHide);
            }

            if (iconLayer != IntPtr.Zero)
            {
                WindowsInterop.ShowWindow(iconLayer, SwHide);
            }

            printed = WindowsInterop.PrintWindow(target, memoryDc, PwRenderFullContent);
            WindowsInterop.GdiFlush();
        }
        finally
        {
            if (iconLayer != IntPtr.Zero)
            {
                WindowsInterop.ShowWindow(iconLayer, SwShowNa);
            }

            if (hideExclude)
            {
                WindowsInterop.ShowWindow(excludeWindow, SwShowNoActivate);
            }
        }

        byte[]? pixels = null;
        if (printed)
        {
            var stride = width * 4;
            pixels = new byte[(long)stride * height];
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(bits + ((cropY + y) * targetWidth + cropX) * 4, pixels, y * stride, stride);
            }
        }

        WindowsInterop.SelectObject(memoryDc, previous);
        WindowsInterop.DeleteObject(dib);
        WindowsInterop.DeleteDC(memoryDc);
        WindowsInterop.ReleaseDC(IntPtr.Zero, screenDc);

        if (pixels is null || IsAllZero(pixels))
        {
            return null;
        }

        return new WallpaperBackground
        {
            BgraPixels = pixels,
            ImageWidth = width,
            ImageHeight = height,
            Position = WallpaperPosition.Stretch,
        };
    }

    private static bool IsAllZero(byte[] pixels)
    {
        var step = Math.Max(4, pixels.Length / 4096 & ~3);
        for (var i = 0; i < pixels.Length - 3; i += step)
        {
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0 || pixels[i + 3] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static (string? Path, WallpaperPosition Position, uint Color)? QueryDesktopWallpaper(
        WindowsInterop.Rect? monitorBounds)
    {
        var balanceInit = WindowsInterop.CoInitializeEx(IntPtr.Zero, CoInitApartmentThreaded) >= 0;
        try
        {
            if (WindowsInterop.CoCreateInstance(
                    in ClsidDesktopWallpaper, IntPtr.Zero, ClsCtxAll, in IidIDesktopWallpaper, out var instance) < 0 ||
                instance == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var vtable = *(void***)instance;
                var path = GetWallpaperPath(instance, vtable, monitorBounds);
                var position = (int)WallpaperPosition.Fill;
                ((delegate* unmanaged<IntPtr, int*, int>)vtable[SlotGetPosition])(instance, &position);
                uint color = 0;
                ((delegate* unmanaged<IntPtr, uint*, int>)vtable[SlotGetBackgroundColor])(instance, &color);
                return (path, (WallpaperPosition)position, color);
            }
            finally
            {
                Marshal.Release(instance);
            }
        }
        finally
        {
            if (balanceInit)
            {
                WindowsInterop.CoUninitialize();
            }
        }
    }

    private static string? GetWallpaperPath(IntPtr instance, void** vtable, WindowsInterop.Rect? monitorBounds)
    {
        var monitorId = FindMonitorId(instance, vtable, monitorBounds);
        var pathPtr = IntPtr.Zero;
        int hr;
        if (monitorId is not null)
        {
            fixed (char* id = monitorId)
            {
                hr = ((delegate* unmanaged<IntPtr, char*, IntPtr*, int>)vtable[SlotGetWallpaper])(
                    instance, id, &pathPtr);
            }
        }
        else
        {
            hr = ((delegate* unmanaged<IntPtr, char*, IntPtr*, int>)vtable[SlotGetWallpaper])(
                instance, null, &pathPtr);
        }

        if (hr < 0 || pathPtr == IntPtr.Zero)
        {
            return null;
        }

        var path = Marshal.PtrToStringUni(pathPtr);
        WindowsInterop.CoTaskMemFree(pathPtr);
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static string? FindMonitorId(IntPtr instance, void** vtable, WindowsInterop.Rect? monitorBounds)
    {
        var targetLeft = monitorBounds?.Left ?? 0;
        var targetTop = monitorBounds?.Top ?? 0;
        uint count = 0;
        if (((delegate* unmanaged<IntPtr, uint*, int>)vtable[SlotGetMonitorDevicePathCount])(instance, &count) < 0)
        {
            return null;
        }

        for (uint i = 0; i < count; i++)
        {
            var idPtr = IntPtr.Zero;
            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)vtable[SlotGetMonitorDevicePathAt])(
                    instance, i, &idPtr) < 0 ||
                idPtr == IntPtr.Zero)
            {
                continue;
            }

            var id = Marshal.PtrToStringUni(idPtr);
            WindowsInterop.CoTaskMemFree(idPtr);
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var rect = default(WindowsInterop.Rect);
            int hr;
            fixed (char* p = id)
            {
                hr = ((delegate* unmanaged<IntPtr, char*, WindowsInterop.Rect*, int>)vtable[SlotGetMonitorRect])(
                    instance, p, &rect);
            }

            if (hr >= 0 && rect.Left == targetLeft && rect.Top == targetTop)
            {
                return id;
            }
        }

        return null;
    }

    private static (string? Path, WallpaperPosition Position, uint Color) QuerySystemParameters()
    {
        var buffer = stackalloc char[MaxPath];
        string? path = null;
        if (WindowsInterop.SystemParametersInfoW(SpiGetDeskWallpaper, MaxPath, buffer, 0))
        {
            path = new string(buffer);
            if (string.IsNullOrWhiteSpace(path))
            {
                path = null;
            }
        }

        return (path, WallpaperPosition.Fill, 0);
    }

    private static (byte[] Pixels, int Width, int Height)? DecodeImage(string path)
    {
        using var stream = File.OpenRead(path);
        using var bitmap = WriteableBitmap.Decode(stream);
        var size = bitmap.PixelSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            return null;
        }

        using var frame = bitmap.Lock();
        var stride = size.Width * 4;
        var pixels = new byte[(long)stride * size.Height];
        for (var y = 0; y < size.Height; y++)
        {
            Marshal.Copy(frame.Address + y * frame.RowBytes, pixels, y * stride, stride);
        }

        if (frame.Format.Equals(PixelFormat.Rgba8888))
        {
            for (var i = 0; i < pixels.Length; i += 4)
            {
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
            }
        }

        return (pixels, size.Width, size.Height);
    }
}
