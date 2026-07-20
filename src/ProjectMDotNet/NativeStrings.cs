using System.Runtime.InteropServices;
using ProjectMDotNet.Interop;

namespace ProjectMDotNet;

internal static unsafe class NativeStrings
{
    internal static string? ConsumeCoreString(sbyte* value)
    {
        if (value is null)
        {
            return null;
        }

        var result = Marshal.PtrToStringUTF8((IntPtr)value);
        NativeMethods.projectm_free_string(value);
        return result;
    }

    internal static string? ConsumePlaylistString(sbyte* value)
    {
        if (value is null)
        {
            return null;
        }

        var result = Marshal.PtrToStringUTF8((IntPtr)value);
        PlaylistNativeMethods.projectm_playlist_free_string(value);
        return result;
    }

    internal static List<string> ConsumePlaylistStringArray(sbyte** array, nuint? count = null)
    {
        var result = new List<string>();
        if (array is null)
        {
            return result;
        }

        if (count is { } n)
        {
            for (nuint i = 0; i < n; i++)
            {
                result.Add(Marshal.PtrToStringUTF8((IntPtr)array[i]) ?? string.Empty);
            }
        }
        else
        {
            for (var i = 0; array[i] is not null; i++)
            {
                result.Add(Marshal.PtrToStringUTF8((IntPtr)array[i]) ?? string.Empty);
            }
        }

        PlaylistNativeMethods.projectm_playlist_free_string_array(array);
        return result;
    }

    internal static void WithUtf8(string value, Action<IntPtr> action)
    {
        var native = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            action(native);
        }
        finally
        {
            Marshal.FreeCoTaskMem(native);
        }
    }

    internal static T WithUtf8<T>(string value, Func<IntPtr, T> func)
    {
        var native = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            return func(native);
        }
        finally
        {
            Marshal.FreeCoTaskMem(native);
        }
    }

    internal static void WithUtf8Array(IReadOnlyList<string> values, Action<IntPtr, nuint> action)
    {
        var pointers = new IntPtr[values.Count];
        try
        {
            for (var i = 0; i < values.Count; i++)
            {
                pointers[i] = Marshal.StringToCoTaskMemUTF8(values[i]);
            }

            fixed (IntPtr* array = pointers)
            {
                action((IntPtr)array, (nuint)values.Count);
            }
        }
        finally
        {
            foreach (var pointer in pointers)
            {
                if (pointer != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
    }
}
