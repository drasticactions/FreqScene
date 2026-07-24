using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FreqScene;

internal static unsafe partial class SeatInterop
{
    private const string Seat = "libseat.so.1";
    private const string Libc = "libc";

    private const int ORdwr = 0x2;
    private const int OCloexec = 0x80000;

    [LibraryImport(Seat)]
    public static partial IntPtr libseat_open_seat(IntPtr listener, IntPtr userdata);

    [LibraryImport(Seat)]
    public static partial int libseat_close_seat(IntPtr seat);

    [LibraryImport(Seat, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int libseat_open_device(IntPtr seat, string path, out int fd);

    [LibraryImport(Seat)]
    public static partial int libseat_close_device(IntPtr seat, int deviceId);

    [LibraryImport(Seat)]
    public static partial int libseat_dispatch(IntPtr seat, int timeoutMs);

    [LibraryImport(Seat)]
    public static partial int libseat_disable_seat(IntPtr seat);

    [LibraryImport(Libc, SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    [LibraryImport(Libc)]
    public static partial int close(int fd);

    public static int OpenReadWrite(string path) => open(path, ORdwr | OCloexec);
}

internal sealed unsafe class SeatSession : IDisposable
{
    private static SeatSession? s_active;
    private static IntPtr s_listener;

    private readonly IntPtr _seat;
    private readonly Dictionary<int, int> _deviceIdsByFd = [];
    private volatile bool _enabled;
    private volatile bool _disableRequested;

    private SeatSession(IntPtr seat)
    {
        _seat = seat;
        _enabled = seat == IntPtr.Zero;
    }

    public bool Enabled => _enabled;

    public bool UsesLibseat => _seat != IntPtr.Zero;

    public static SeatSession Open()
    {
        if (s_active is not null)
        {
            throw new InvalidOperationException("Only one seat session can be active per process.");
        }

        IntPtr seat = IntPtr.Zero;
        try
        {
            if (s_listener == IntPtr.Zero)
            {
                var listener = (IntPtr*)NativeMemory.Alloc((nuint)(sizeof(IntPtr) * 2));
                listener[0] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnEnableSeat;
                listener[1] = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnDisableSeat;
                s_listener = (IntPtr)listener;
            }

            seat = SeatInterop.libseat_open_seat(s_listener, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            // No libseat on this box; the direct-open fallback below still works for
            // root or the video group when no other DRM master is around.
        }

        var session = new SeatSession(seat);
        s_active = session;

        if (seat != IntPtr.Zero)
        {
            // logind delivers the initial enable asynchronously.
            var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 5;
            while (!session._enabled && Stopwatch.GetTimestamp() < deadline)
            {
                if (SeatInterop.libseat_dispatch(seat, 100) < 0)
                {
                    break;
                }
            }

            if (!session._enabled)
            {
                session.Dispose();
                throw new InvalidOperationException(
                    "The seat did not become active within 5 seconds (is another session using this seat?).");
            }
        }
        else
        {
            Trace.TraceInformation("[kms] libseat unavailable; opening DRM devices directly.");
        }

        return session;
    }

    public int OpenDevice(string path)
    {
        if (_seat != IntPtr.Zero)
        {
            var deviceId = SeatInterop.libseat_open_device(_seat, path, out var fd);
            if (deviceId < 0)
            {
                return -1;
            }

            _deviceIdsByFd[fd] = deviceId;
            return fd;
        }

        var directFd = SeatInterop.OpenReadWrite(path);
        if (directFd >= 0)
        {
            DrmInterop.drmSetMaster(directFd);
        }

        return directFd;
    }

    public void CloseDevice(int fd)
    {
        if (fd < 0)
        {
            return;
        }

        if (_seat != IntPtr.Zero)
        {
            if (_deviceIdsByFd.Remove(fd, out var deviceId))
            {
                SeatInterop.libseat_close_device(_seat, deviceId);
            }

            return;
        }

        SeatInterop.close(fd);
    }

    public void Dispatch()
    {
        if (_seat == IntPtr.Zero)
        {
            return;
        }

        SeatInterop.libseat_dispatch(_seat, 0);
        if (_disableRequested)
        {
            _disableRequested = false;
            SeatInterop.libseat_disable_seat(_seat);
        }
    }

    public void Dispose()
    {
        if (_seat != IntPtr.Zero)
        {
            SeatInterop.libseat_close_seat(_seat);
        }

        if (s_active == this)
        {
            s_active = null;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEnableSeat(IntPtr seat, IntPtr userdata)
    {
        if (s_active is { } session)
        {
            session._enabled = true;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnDisableSeat(IntPtr seat, IntPtr userdata)
    {
        if (s_active is { } session)
        {
            session._enabled = false;
            session._disableRequested = true;
        }
    }
}
