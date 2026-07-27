using Microsoft.Extensions.Logging;
using ProjectMDotNet;

namespace FreqScene.iOS;

internal static class AppLog
{
    public static ILoggerFactory Factory { get; } = CreateFactory();

    public static ILogger For(string category) => Factory.CreateLogger(category);

    private static ILoggerFactory CreateFactory()
    {
        var factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            builder.AddFilter("Grpc", Microsoft.Extensions.Logging.LogLevel.Warning);
            builder.AddProvider(new OsLogLoggerProvider());
        });

        var projectM = factory.CreateLogger("projectM");
        ProjectMLog.Message += (message, level) =>
            projectM.Log(
                level switch
                {
                    ProjectMDotNet.LogLevel.Trace => Microsoft.Extensions.Logging.LogLevel.Trace,
                    ProjectMDotNet.LogLevel.Debug => Microsoft.Extensions.Logging.LogLevel.Debug,
                    ProjectMDotNet.LogLevel.Info => Microsoft.Extensions.Logging.LogLevel.Information,
                    ProjectMDotNet.LogLevel.Warning => Microsoft.Extensions.Logging.LogLevel.Warning,
                    _ => Microsoft.Extensions.Logging.LogLevel.Error,
                },
                "{Message}",
                message);

        return factory;
    }
}
