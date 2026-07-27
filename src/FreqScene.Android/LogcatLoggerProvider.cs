using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using AndroidLog = Android.Util.Log;

namespace FreqScene.Android;

internal sealed class LogcatLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, LogcatLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static name => new LogcatLogger(Tag(name)));

    public void Dispose()
    {
    }

    private static string Tag(string category)
    {
        var tag = category[(category.LastIndexOf('.') + 1)..];
        return tag.Length <= 23 ? tag : tag[..23];
    }

    private sealed class LogcatLogger(string tag) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message = $"{message}\n{exception}";
            }

            switch (logLevel)
            {
                case LogLevel.Trace:
                    AndroidLog.Verbose(tag, message);
                    break;
                case LogLevel.Debug:
                    AndroidLog.Debug(tag, message);
                    break;
                case LogLevel.Information:
                    AndroidLog.Info(tag, message);
                    break;
                case LogLevel.Warning:
                    AndroidLog.Warn(tag, message);
                    break;
                default:
                    AndroidLog.Error(tag, message);
                    break;
            }
        }
    }
}
