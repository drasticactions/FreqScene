using Microsoft.Extensions.Logging;

namespace FreqScene;

internal static class DisplayTargets
{
    public static IReadOnlyList<DisplayInfo> List(ILogger logger)
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
            logger.LogError(ex, "display enumeration failed");
        }

        return [];
    }
}
