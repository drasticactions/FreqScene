using System.Runtime.InteropServices;

namespace FreqScene;

internal static partial class GbmInterop
{
    private const string Gbm = "libgbm.so.1";

    public const uint GbmFormatArgb8888 = 0x34325241; // fourcc 'AR24'
    public const uint GbmBoUseScanout = 1 << 0;
    public const uint GbmBoUseRendering = 1 << 2;

    [LibraryImport(Gbm)]
    public static partial IntPtr gbm_create_device(int fd);

    [LibraryImport(Gbm)]
    public static partial void gbm_device_destroy(IntPtr device);

    [LibraryImport(Gbm)]
    public static partial IntPtr gbm_surface_create(IntPtr device, uint width, uint height, uint format, uint flags);

    [LibraryImport(Gbm)]
    public static partial void gbm_surface_destroy(IntPtr surface);

    [LibraryImport(Gbm)]
    public static partial IntPtr gbm_surface_lock_front_buffer(IntPtr surface);

    [LibraryImport(Gbm)]
    public static partial void gbm_surface_release_buffer(IntPtr surface, IntPtr bo);

    [LibraryImport(Gbm)]
    public static partial ulong gbm_bo_get_handle(IntPtr bo);

    [LibraryImport(Gbm)]
    public static partial uint gbm_bo_get_stride(IntPtr bo);
}
