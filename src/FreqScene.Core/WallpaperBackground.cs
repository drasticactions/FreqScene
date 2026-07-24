namespace FreqScene;

public enum WallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5,
}

public sealed class WallpaperBackground
{
    public byte[]? BgraPixels { get; init; }

    public int ImageWidth { get; init; }

    public int ImageHeight { get; init; }

    public WallpaperPosition Position { get; init; }

    public float BackgroundRed { get; init; }

    public float BackgroundGreen { get; init; }

    public float BackgroundBlue { get; init; }

    public int SpanX { get; init; }

    public int SpanY { get; init; }

    public int SpanWidth { get; init; }

    public int SpanHeight { get; init; }
}
