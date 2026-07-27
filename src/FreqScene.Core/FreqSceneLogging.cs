using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Formatters;
using ZLogger.Providers;

namespace FreqScene;

public static class FreqSceneLogging
{
    public const string EnvironmentVariable = "FREQSCENE_LOG";

    private const int MaxLogFiles = 3;
    private const int RollSizeKB = 1024;

    public static ILoggerFactory Create(
        string headName,
        LogLevel minimumLevel = LogLevel.Information,
        bool console = true,
        string? logDirectory = null)
    {
        var directory = logDirectory ?? Path.Combine(AppDataDirectory.Default, "logs");
        var overrides = ParseEnvironment(Environment.GetEnvironmentVariable(EnvironmentVariable), ref minimumLevel);

        return LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(minimumLevel);

            builder.AddFilter("Microsoft", LogLevel.Warning);
            builder.AddFilter("Grpc", LogLevel.Warning);
            foreach (var (category, level) in overrides)
            {
                builder.AddFilter(category, level);
            }

            if (console)
            {
                builder.AddZLoggerConsole(options =>
                {
                    options.LogToStandardErrorThreshold = LogLevel.Trace;
                    options.UsePlainTextFormatter(ConfigurePlainText);
                });
            }

            TryAddRollingFile(builder, directory, headName);
        });
    }

    public static LogLevel? TryParseLevel(string? text) =>
        Enum.TryParse<LogLevel>(text, ignoreCase: true, out var level) ? level : null;

    public static void AttachProjectMLog(ILoggerFactory factory)
    {
        var logger = factory.CreateLogger("projectM");
        try
        {
            ProjectMDotNet.ProjectMLog.Message += (message, level) =>
                logger.Log(
                    level switch
                    {
                        ProjectMDotNet.LogLevel.Trace => LogLevel.Trace,
                        ProjectMDotNet.LogLevel.Debug => LogLevel.Debug,
                        ProjectMDotNet.LogLevel.Info => LogLevel.Information,
                        ProjectMDotNet.LogLevel.Warning => LogLevel.Warning,
                        _ => LogLevel.Error,
                    },
                    "{Message}",
                    message);
        }
        catch (DllNotFoundException)
        {
        }
    }

    private static void TryAddRollingFile(ILoggingBuilder builder, string directory, string headName)
    {
        try
        {
            Directory.CreateDirectory(directory);
            PruneOldFiles(directory, headName);
        }
        catch (Exception)
        {
            return;
        }

        builder.AddZLoggerRollingFile(options =>
        {
            options.FilePathSelector = (timestamp, sequence) =>
                Path.Combine(directory, $"{headName}-{timestamp.ToLocalTime():yyyyMMdd}-{sequence:000}.log");
            options.RollingInterval = RollingInterval.Day;
            options.RollingSizeKB = RollSizeKB;
            options.UsePlainTextFormatter(ConfigurePlainText);
        });
    }

    private static void PruneOldFiles(string directory, string headName)
    {
        var files = new DirectoryInfo(directory).GetFiles($"{headName}-*.log");
        if (files.Length < MaxLogFiles)
        {
            return;
        }

        Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
        for (var i = MaxLogFiles - 1; i < files.Length; i++)
        {
            try
            {
                files[i].Delete();
            }
            catch (IOException)
            {
            }
        }
    }

    private static void ConfigurePlainText(PlainTextZLoggerFormatter formatter) =>
        formatter.SetPrefixFormatter(
            $"{0:local-longdate} [{1:short}] {2}: ",
            (in MessageTemplate template, in LogInfo info) =>
                template.Format(info.Timestamp, info.LogLevel, info.Category));

    private static List<(string Category, LogLevel Level)> ParseEnvironment(string? spec, ref LogLevel minimumLevel)
    {
        var overrides = new List<(string, LogLevel)>();
        if (string.IsNullOrWhiteSpace(spec))
        {
            return overrides;
        }

        foreach (var token in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = token.IndexOf('=');
            if (eq < 0)
            {
                if (TryParseLevel(token) is { } level)
                {
                    minimumLevel = level;
                }
            }
            else if (TryParseLevel(token[(eq + 1)..]) is { } level)
            {
                overrides.Add((token[..eq].Trim(), level));
            }
        }

        return overrides;
    }
}
