using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FreqScene;

[SupportedOSPlatform("windows10.0.14393")]
internal static unsafe class WindowsDisplays
{
    private static List<(string Device, RECT Bounds, bool IsPrimary)>? s_monitors;

    public static IReadOnlyList<DisplayInfo> List()
    {
        var result = new List<DisplayInfo>();
        foreach (var (device, bounds, isPrimary) in Enumerate())
        {
            var name = DisplayName(device, result.Count + 1);
            var label = $"{name} ({bounds.Width}×{bounds.Height})";
            result.Add(new DisplayInfo(device, isPrimary ? label + " — Primary" : label, isPrimary));
        }

        return result;
    }

    public static RECT ResolveBounds(string? key)
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

        return new RECT
        {
            right = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN),
            bottom = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN),
        };
    }

    private static List<(string Device, RECT Bounds, bool IsPrimary)> Enumerate()
    {
        var monitors = s_monitors = [];
        PInvoke.EnumDisplayMonitors(HDC.Null, null, &MonitorCallback, default);
        s_monitors = null;
        return monitors;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static BOOL MonitorCallback(HMONITOR monitor, HDC dc, RECT* rect, LPARAM data)
    {
        var info = default(MONITORINFOEXW);
        info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
        if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
        {
            s_monitors?.Add((
                info.szDevice.ToString(),
                info.monitorInfo.rcMonitor,
                (info.monitorInfo.dwFlags & PInvoke.MONITORINFOF_PRIMARY) != 0));
        }

        return true; // continue enumeration
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
