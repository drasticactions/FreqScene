using System.Runtime.InteropServices;

namespace FreqScene;

internal static unsafe partial class OpenAlCapture
{
    internal const int FormatStereo16 = 0x1103;
    internal const int CaptureSamplesParam = 0x312;

    private const int CaptureDeviceSpecifier = 0x310;
    private const string LogicalName = "openal-capture";

    private static readonly Lazy<IntPtr> Library = new(LoadLibrary);

    internal static bool IsAvailable => Library.Value != IntPtr.Zero;

    static OpenAlCapture() =>
        NativeLibrary.SetDllImportResolver(typeof(OpenAlCapture).Assembly, (name, _, _) =>
            name == LogicalName ? Library.Value : IntPtr.Zero);

    /// <summary>
    /// Names of all available capture devices, or empty when OpenAL is unavailable.
    /// </summary>
    internal static IReadOnlyList<string> GetCaptureDevices()
    {
        var devices = new List<string>();
        if (!IsAvailable)
        {
            return devices;
        }

        // Null-separated, double-null-terminated UTF-8 list.
        var cursor = (byte*)alcGetString(IntPtr.Zero, CaptureDeviceSpecifier);
        while (cursor is not null && *cursor != 0)
        {
            var name = Marshal.PtrToStringUTF8((IntPtr)cursor);
            if (!string.IsNullOrEmpty(name))
            {
                devices.Add(name);
            }

            while (*cursor != 0)
            {
                cursor++;
            }

            cursor++;
        }

        return devices;
    }

    [LibraryImport(LogicalName, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial IntPtr alcCaptureOpenDevice(string? devicename, uint frequency, int format, int buffersize);

    [LibraryImport(LogicalName)]
    internal static partial byte alcCaptureCloseDevice(IntPtr device);

    [LibraryImport(LogicalName)]
    internal static partial void alcCaptureStart(IntPtr device);

    [LibraryImport(LogicalName)]
    internal static partial void alcCaptureStop(IntPtr device);

    [LibraryImport(LogicalName)]
    internal static partial void alcCaptureSamples(IntPtr device, void* buffer, int samples);

    [LibraryImport(LogicalName)]
    internal static partial void alcGetIntegerv(IntPtr device, int param, int size, int* values);

    [LibraryImport(LogicalName)]
    private static partial IntPtr alcGetString(IntPtr device, int param);

    private static IntPtr LoadLibrary()
    {
        string[] candidates = OperatingSystem.IsMacOS()
            ? ["libopenal.dylib", "openal", "/opt/homebrew/lib/libopenal.dylib", "/usr/local/lib/libopenal.dylib", "/System/Library/Frameworks/OpenAL.framework/OpenAL"]
            : OperatingSystem.IsWindows()
                ? ["openal", "soft_oal.dll", "openal32.dll"]
                : ["openal", "libopenal.so.1", "libopenal.so"];

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, typeof(OpenAlCapture).Assembly, null, out var handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }
}
