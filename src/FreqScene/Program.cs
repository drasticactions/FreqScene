using Avalonia;
using System;

namespace FreqScene;

class Program
{
    /// <summary>--log-level from the command line; App reads it when building the logger factory.</summary>
    internal static Microsoft.Extensions.Logging.LogLevel? LogLevelOverride { get; private set; }

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--log-level")
            {
                LogLevelOverride = FreqSceneLogging.TryParseLevel(args[i + 1]);
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace(global::Avalonia.Logging.LogEventLevel.Information);

        if (OperatingSystem.IsWindows())
        {
            builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Wgl, Win32RenderingMode.Software],
            });
        }

        if (OperatingSystem.IsMacOS())
        {
            builder = builder
                .With(new AvaloniaNativePlatformOptions
                {
                    RenderingMode = [AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software],
                })
                .With(new MacOSPlatformOptions
                {
                    ShowInDock = false,
                });
        }

        if (OperatingSystem.IsLinux())
        {
            builder = builder.UseWayland();
        }

        return builder;
    }
}
