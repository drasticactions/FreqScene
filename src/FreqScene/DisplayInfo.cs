namespace FreqScene;

internal static class DisplayTargets
{
    public static IReadOnlyList<DisplayInfo> List()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return MacDisplays.List();
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
            {
                return WindowsDisplays.List();
            }

            if (OperatingSystem.IsLinux())
            {
                return LinuxWaylandSession.ListOutputs();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"[native] display enumeration failed: {ex}");
        }

        return [];
    }
}
