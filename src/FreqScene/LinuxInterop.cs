using System.Runtime.InteropServices;

namespace FreqScene;

internal static partial class LinuxInterop
{
    private const string Egl = "libEGL.so.1";
    private const string WaylandEgl = "libwayland-egl.so.1";
    private const string Libc = "libc";

    public const int EglSurfaceType = 0x3033;
    public const int EglWindowBit = 0x0004;
    public const int EglRenderableType = 0x3040;
    public const int EglOpenGlBit = 0x0008;
    public const int EglRedSize = 0x3024;
    public const int EglGreenSize = 0x3023;
    public const int EglBlueSize = 0x3022;
    public const int EglAlphaSize = 0x3021;
    public const int EglDepthSize = 0x3025;
    public const int EglNone = 0x3038;
    public const int EglContextMajorVersion = 0x3098;
    public const int EglContextMinorVersion = 0x30FB;
    public const int EglContextOpenGlProfileMask = 0x30FD;
    public const int EglContextOpenGlCoreProfileBit = 0x0001;
    public const uint EglOpenGlApi = 0x30A2;
    public const uint EglPlatformWaylandKhr = 0x31D8;

    [LibraryImport(Egl)]
    public static partial IntPtr eglGetPlatformDisplay(uint platform, IntPtr nativeDisplay, IntPtr attribs);

    [LibraryImport(Egl)]
    public static partial IntPtr eglGetDisplay(IntPtr nativeDisplay);

    [LibraryImport(Egl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool eglInitialize(IntPtr display, out int major, out int minor);

    [LibraryImport(Egl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool eglTerminate(IntPtr display);

    [LibraryImport(Egl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool eglBindAPI(uint api);

    [LibraryImport(Egl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static unsafe partial bool eglChooseConfig(
        IntPtr display, int* attribs, out IntPtr config, int configSize, out int configCount);

    [LibraryImport(Egl)]
    public static unsafe partial IntPtr eglCreateContext(
        IntPtr display, IntPtr config, IntPtr shareContext, int* attribs);

    [LibraryImport(Egl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool eglDestroyContext(IntPtr display, IntPtr context);

    [LibraryImport(Egl)]
    public static unsafe partial IntPtr eglCreateWindowSurface(
        IntPtr display, IntPtr config, IntPtr nativeWindow, int* attribs);

    [LibraryImport(Egl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool eglDestroySurface(IntPtr display, IntPtr surface);

    [LibraryImport(Egl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool eglMakeCurrent(IntPtr display, IntPtr draw, IntPtr read, IntPtr context);

    [LibraryImport(Egl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool eglSwapBuffers(IntPtr display, IntPtr surface);

    [LibraryImport(Egl)]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool eglSwapInterval(IntPtr display, int interval);

    [LibraryImport(Egl, StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr eglGetProcAddress(string name);

    [LibraryImport(Egl)]
    public static partial int eglGetError();

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

    /// <summary>Returns true when the file descriptor has data to read (non-blocking check).</summary>
    public static bool PollReadable(int fd)
    {
        var pollFd = new PollFd { Fd = fd, Events = PollIn };
        return poll(ref pollFd, 1, 0) > 0 && (pollFd.Revents & PollIn) != 0;
    }
}
