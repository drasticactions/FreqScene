using System.Diagnostics;
using System.Globalization;

namespace FreqScene.Cli;

// Routes Trace output (the logging idiom used across FreqScene) to the
// console. Informational and higher events always print; Trace.Write/WriteLine
// carry verbose diagnostics (e.g. libseat debug messages) and only print with
// --verbose.
internal sealed class CliTraceListener(bool verbose) : TraceListener
{
    public override void Write(string? message)
    {
        if (verbose && message is not null)
        {
            Console.Write(message);
        }
    }

    public override void WriteLine(string? message)
    {
        if (verbose)
        {
            Console.WriteLine(message);
        }
    }

    public override void TraceEvent(
        TraceEventCache? eventCache, string source, TraceEventType eventType, int id, string? message)
    {
        var target = eventType <= TraceEventType.Warning ? Console.Error : Console.Out;
        target.WriteLine(message);
    }

    public override void TraceEvent(
        TraceEventCache? eventCache, string source, TraceEventType eventType, int id,
        string? format, params object?[]? args)
        => TraceEvent(eventCache, source, eventType, id,
            format is not null && args is { Length: > 0 }
                ? string.Format(CultureInfo.InvariantCulture, format, args)
                : format);
}
