using System.Text.Json.Serialization;

namespace FreqScene;

[JsonConverter(typeof(JsonStringEnumConverter<DisplayMode>))]
public enum DisplayMode
{
    Window,

    Overlay,

    Wallpaper,
}

public static class DisplayModes
{
    public static IReadOnlyList<DisplayMode> Available { get; } =
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()
            ? [DisplayMode.Window, DisplayMode.Overlay, DisplayMode.Wallpaper]
            : [DisplayMode.Window];

    public static DisplayMode Normalize(DisplayMode mode) =>
        Available.Contains(mode) ? mode : DisplayMode.Window;

    public static string Label(DisplayMode mode) => mode switch
    {
        DisplayMode.Overlay => "Overlay",
        DisplayMode.Wallpaper => "Wallpaper",
        _ => "Window",
    };
}
