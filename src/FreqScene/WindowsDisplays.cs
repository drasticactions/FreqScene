using System.Runtime.InteropServices;

namespace FreqScene;

internal static unsafe class WindowsDisplays
{
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const uint MonitorInfoPrimary = 1;

    private static List<(string Device, WindowsInterop.Rect Bounds, bool IsPrimary)>? s_monitors;

    public static IReadOnlyList<DisplayInfo> List()
    {
        var result = new List<DisplayInfo>();
        foreach (var (device, bounds, isPrimary) in Enumerate())
        {
            var name = DisplayName(device, result.Count + 1);
            var label = $"{name} ({bounds.Right - bounds.Left}×{bounds.Bottom - bounds.Top})";
            result.Add(new DisplayInfo(device, isPrimary ? label + " — Primary" : label, isPrimary));
        }

        return result;
    }

    public static WindowsInterop.Rect ResolveBounds(string? key)
    {
        var monitors = Enumerate();
        foreach (var (device, bounds, _) in monitors)
        {
            if (device == key)
            {
                return bounds;
            }
        }

        foreach (var (_, bounds, isPrimary) in monitors)
        {
            if (isPrimary)
            {
                return bounds;
            }
        }

        return new WindowsInterop.Rect
        {
            Right = WindowsInterop.GetSystemMetrics(SmCxScreen),
            Bottom = WindowsInterop.GetSystemMetrics(SmCyScreen),
        };
    }

    private static List<(string Device, WindowsInterop.Rect Bounds, bool IsPrimary)> Enumerate()
    {
        var monitors = s_monitors = [];
        WindowsInterop.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &MonitorCallback, IntPtr.Zero);
        s_monitors = null;
        return monitors;
    }

    [UnmanagedCallersOnly]
    private static int MonitorCallback(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data)
    {
        var info = new WindowsInterop.MonitorInfoExW { Size = (uint)sizeof(WindowsInterop.MonitorInfoExW) };
        if (WindowsInterop.GetMonitorInfoW(monitor, ref info))
        {
            var device = new ReadOnlySpan<char>(info.Device, 32);
            var terminator = device.IndexOf('\0');
            s_monitors?.Add((
                new string(terminator < 0 ? device : device[..terminator]),
                info.Monitor,
                (info.Flags & MonitorInfoPrimary) != 0));
        }

        return 1; // continue enumeration
    }

    private static string DisplayName(string device, int ordinal)
    {
        // "\\.\DISPLAY2" → "Display 2"
        var digits = device.TrimEnd();
        var start = digits.Length;
        while (start > 0 && char.IsAsciiDigit(digits[start - 1]))
        {
            start--;
        }

        return start < digits.Length ? $"Display {digits[start..]}" : $"Display {ordinal}";
    }
}
