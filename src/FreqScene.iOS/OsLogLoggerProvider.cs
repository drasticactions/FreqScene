using System.Collections.Concurrent;
using CoreFoundation;
using Microsoft.Extensions.Logging;

namespace FreqScene.iOS;

internal sealed class OsLogLoggerProvider : ILoggerProvider
{
    private const string Subsystem = "com.freqscene";

    private readonly ConcurrentDictionary<string, OsLogger> _loggers = new();

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static name => new OsLogger(name));

    public void Dispose()
    {
    }

    private sealed class OsLogger(string category) : ILogger
    {
        private readonly CoreFoundation.OSLog _log = new(Subsystem, category);

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

            _log.Log(ToOsLogLevel(logLevel), message);
        }

        private static OSLogLevel ToOsLogLevel(LogLevel level) => level switch
        {
            LogLevel.Trace or LogLevel.Debug => OSLogLevel.Debug,
            LogLevel.Information => OSLogLevel.Info,
            LogLevel.Warning => OSLogLevel.Default,
            LogLevel.Error => OSLogLevel.Error,
            _ => OSLogLevel.Fault,
        };
    }
}
