using System.Runtime.InteropServices;

namespace FreqScene;

internal static partial class LinuxInterop
{
    private const string WaylandEgl = "libwayland-egl.so.1";
    private const string Libc = "libc";

    [LibraryImport(WaylandEgl)]
    public static partial IntPtr wl_egl_window_create(IntPtr surface, int width, int height);

    [LibraryImport(WaylandEgl)]
    public static partial void wl_egl_window_destroy(IntPtr window);

    [LibraryImport(WaylandEgl)]
    public static partial void wl_egl_window_resize(IntPtr window, int width, int height, int dx, int dy);

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    private const short PollIn = 0x0001;

    [LibraryImport(Libc, SetLastError = true)]
    private static partial int poll(ref PollFd fds, ulong count, int timeoutMs);

    public static bool PollReadable(int fd) => PollReadable(fd, 0);

    public static bool PollReadable(int fd, int timeoutMs)
    {
        var pollFd = new PollFd { Fd = fd, Events = PollIn };
        return poll(ref pollFd, 1, timeoutMs) > 0 && (pollFd.Revents & PollIn) != 0;
    }
}
