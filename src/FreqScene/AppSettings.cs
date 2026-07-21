namespace FreqScene;

public sealed class AppSettings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Window;

    public int RenderScalePercent { get; set; } = QualityOptions.DefaultRenderScalePercent;

    public int FrameRateCap { get; set; } = QualityOptions.DefaultFrameRateCap;
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
