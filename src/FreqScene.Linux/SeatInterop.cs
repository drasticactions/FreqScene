using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Seatd;

namespace FreqScene;

internal static partial class SeatInterop
{
    private const string Libc = "libc";

    private const int ORdwr = 0x2;
    private const int OCloexec = 0x80000;

    [LibraryImport(Libc, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    [LibraryImport(Libc)]
    public static partial int close(int fd);

    public static int OpenReadWrite(string path) => open(path, ORdwr | OCloexec);
}

internal sealed class SeatSession : IDisposable
{
    private readonly Seat? _seat;
    private readonly Dictionary<int, SeatDevice> _devicesByFd = [];

    private SeatSession(Seat? seat) => _seat = seat;

    public bool Enabled => _seat?.IsActive ?? true;

    public bool UsesLibseat => _seat is not null;

    public static SeatSession Open(ILogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        Seat? seat = null;
        try
        {
            InstallLogForwarder(logger);
            seat = Seat.Open();
        }
        catch (DllNotFoundException)
        {
            // No libseat on this box; the direct-open fallback below still works for
            // root or the video group when no other DRM master is around.
            logger.LogInformation("libseat is not installed; opening DRM devices directly.");
        }
        catch (SeatdException ex)
        {
            // libseat is present but no seat provider (seatd/logind) took us; same
            // direct-open fallback applies.
            logger.LogInformation("libseat could not open a seat ({Reason}); opening DRM devices directly.", ex.Message);
        }

        if (seat is null)
        {
            return new SeatSession(null);
        }

        try
        {
            // logind delivers the initial enable asynchronously.
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            while (!seat.IsActive && Stopwatch.GetTimestamp() < deadline)
            {
                try
                {
                    seat.Dispatch(100);
                }
                catch (SeatdException)
                {
                    break;
                }
            }

            if (!seat.IsActive)
            {
                throw new InvalidOperationException(
                    "The seat did not become active within 5 seconds (is another session using this seat?).");
            }
        }
        catch
        {
            seat.Dispose();
            throw;
        }

        string seatName;
        try
        {
            seatName = seat.Name;
        }
        catch (SeatdException)
        {
            // The name is only for the log line; never let it fail the session.
            seatName = "unknown";
        }

        logger.LogInformation("using libseat for session control (seat: {SeatName}).", seatName);
        return new SeatSession(seat);
    }

    private static void InstallLogForwarder(ILogger logger)
    {
        // Ask libseat for everything and let the logger's level filters decide.
        SeatLog.SetHandler((level, message) =>
        {
            switch (level)
            {
                case SeatLogLevel.Error:
                    logger.LogError("libseat: {Message}", message);
                    break;
                case SeatLogLevel.Debug:
                    logger.LogDebug("libseat: {Message}", message);
                    break;
                default:
                    logger.LogInformation("libseat: {Message}", message);
                    break;
            }
        });
        SeatLog.SetLevel(SeatLogLevel.Debug);
    }

    public int OpenDevice(string path)
    {
        if (_seat is not null)
        {
            try
            {
                var device = _seat.OpenDevice(path);
                _devicesByFd[device.FileDescriptor] = device;
                return device.FileDescriptor;
            }
            catch (SeatdException)
            {
                return -1;
            }
        }

        var directFd = SeatInterop.OpenReadWrite(path);
        if (directFd >= 0)
        {
            Drm.Native.Libdrm.drmSetMaster(directFd);
        }

        return directFd;
    }

    public void CloseDevice(int fd)
    {
        if (fd < 0)
        {
            return;
        }

        if (_seat is not null)
        {
            if (_devicesByFd.Remove(fd, out var device))
            {
                device.Dispose();
            }

            return;
        }

        SeatInterop.close(fd);
    }

    public void Dispatch()
    {
        if (_seat is null)
        {
            return;
        }

        try
        {
            _seat.Dispatch(0);
        }
        catch (SeatdException)
        {
            // A dead seatd/logind connection must not take down the render loop;
            // rendering pauses via Enabled until the seat comes back.
        }
    }

    public void Dispose()
    {
        foreach (var device in _devicesByFd.Values)
        {
            device.Dispose();
        }

        _devicesByFd.Clear();
        _seat?.Dispose();
    }
}
