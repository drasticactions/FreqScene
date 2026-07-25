using System.Diagnostics;
using System.Runtime.InteropServices;
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

    public static SeatSession Open()
    {
        Seat? seat = null;
        try
        {
            InstallLogForwarder();
            seat = Seat.Open();
        }
        catch (DllNotFoundException)
        {
            // No libseat on this box; the direct-open fallback below still works for
            // root or the video group when no other DRM master is around.
            Emit("libseat is not installed; opening DRM devices directly.");
        }
        catch (SeatdException ex)
        {
            // libseat is present but no seat provider (seatd/logind) took us; same
            // direct-open fallback applies.
            Emit($"libseat could not open a seat ({ex.Message}); opening DRM devices directly.");
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

        Emit($"using libseat for session control (seat: {seatName}).");
        return new SeatSession(seat);
    }

    private static void InstallLogForwarder()
    {
        // Ask libseat for everything and let trace listeners filter: debug
        // goes out as verbose Trace.WriteLine, the rest at matching levels.
        SeatLog.SetHandler((level, message) =>
        {
            switch (level)
            {
                case SeatLogLevel.Error:
                    Trace.TraceError($"[kms] libseat: {message}");
                    break;
                case SeatLogLevel.Debug:
                    Trace.WriteLine($"[kms] libseat: {message}");
                    break;
                default:
                    Trace.TraceInformation($"[kms] libseat: {message}");
                    break;
            }
        });
        SeatLog.SetLevel(SeatLogLevel.Debug);
    }

    private static void Emit(string message) => Trace.TraceInformation($"[kms] {message}");

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
