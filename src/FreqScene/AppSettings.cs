namespace FreqScene;

public sealed class AppSettings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Window;

    public bool WallpaperTransparency { get; set; } = true;

    public string? PreferredDisplay { get; set; }

    public int RenderScalePercent { get; set; } = QualityOptions.DefaultRenderScalePercent;

    public int FrameRateCap { get; set; } = QualityOptions.DefaultFrameRateCap;

    public bool VisualizerStopped { get; set; }

    public bool AllowRemoteConnections { get; set; }

    public bool BroadcastServer { get; set; } = true;

    public int RemotePort { get; set; } = Remote.RemoteProtocol.DefaultPort;

    public string? ServerDisplayName { get; set; }
}

public static class QualityOptions
{
    public const int DefaultRenderScalePercent = 100;

    public const int DefaultFrameRateCap = 60;

    public static IReadOnlyList<int> RenderScalePercents { get; } = [100, 75, 50, 25];

    public static IReadOnlyList<int> FrameRateCaps { get; } = [0, 60, 30];

    public static int NormalizeRenderScale(int percent) =>
        RenderScalePercents.Contains(percent) ? percent : DefaultRenderScalePercent;

    public static int NormalizeFrameRate(int cap) =>
        FrameRateCaps.Contains(cap) ? cap : DefaultFrameRateCap;

    public static string FrameRateLabel(int cap) => cap == 0 ? "Display" : $"{cap} FPS";
}
