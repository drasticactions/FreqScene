namespace FreqScene;

public static class DisplayModes
{
    public static IReadOnlyList<DisplayMode> Available { get; } = ComputeAvailable();

    private static IReadOnlyList<DisplayMode> ComputeAvailable()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            return [DisplayMode.Window, DisplayMode.Overlay, DisplayMode.Wallpaper];
        }

        // Linux overlay/wallpaper need the compositor to support wlr-layer-shell
        if (OperatingSystem.IsLinux() && LinuxWaylandSession.HasLayerShell())
        {
            return [DisplayMode.Window, DisplayMode.Overlay, DisplayMode.Wallpaper];
        }

        return [DisplayMode.Window];
    }

    public static DisplayMode Normalize(DisplayMode mode) =>
        Available.Contains(mode) ? mode : DisplayMode.Window;

    public static string Label(DisplayMode mode) => mode switch
    {
        DisplayMode.Overlay => "Overlay",
        DisplayMode.Wallpaper => "Wallpaper",
        _ => "Window",
    };
}
