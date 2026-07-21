using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ProjectMDotNet.Interop;

internal static class NativeLoader
{
    internal const string CoreLibrary = "projectM-4";
    internal const string PlaylistLibrary = "projectM-4-playlist";

    private static readonly object Sync = new();
    private static IntPtr _core;
    private static IntPtr _playlist;

    [ModuleInitializer]
    internal static void Initialize() =>
        NativeLibrary.SetDllImportResolver(typeof(NativeLoader).Assembly, Resolve);

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (OperatingSystem.IsIOS() &&
            libraryName is CoreLibrary or PlaylistLibrary)
        {
            return NativeLibrary.GetMainProgramHandle();
        }

        switch (libraryName)
        {
            case CoreLibrary:
                return LoadCore(assembly);
            case PlaylistLibrary:
                lock (Sync)
                {
                    if (_playlist == IntPtr.Zero)
                    {
                        // Loading core first satisfies the playlist library's dependency
                        // on the exact same core image (SONAME match on Linux,
                        // @loader_path on macOS, same-directory probing on Windows).
                        LoadCore(assembly);
                        _playlist = LoadFirst(PlaylistCandidates(), assembly);
                    }

                    return _playlist;
                }
            default:
                return IntPtr.Zero;
        }
    }

    private static IntPtr LoadCore(Assembly assembly)
    {
        lock (Sync)
        {
            if (_core == IntPtr.Zero)
            {
                _core = LoadFirst(CoreCandidates(), assembly);
            }

            return _core;
        }
    }

    private static IntPtr LoadFirst(IEnumerable<string> candidates, Assembly assembly)
    {
        foreach (var candidate in candidates)
        {
            foreach (var directory in ProbeDirectories())
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var fromPath))
                {
                    return fromPath;
                }
            }

            if (NativeLibrary.TryLoad(candidate, assembly, null, out var fromDefault))
            {
                return fromDefault;
            }
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<string> CoreCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "projectM-4.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "libprojectM-4.4.dylib";
            yield return "libprojectM-4.dylib";
        }
        else if (OperatingSystem.IsAndroid())
        {
            // Unversioned soname; resolved from the app's native-lib directory.
            yield return "libprojectM-4.so";
        }
        else
        {
            yield return "libprojectM-4.so.4";
            yield return "libprojectM-4.so";
        }
    }

    private static IEnumerable<string> PlaylistCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return "projectM-4-playlist.dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "libprojectM-4-playlist.4.dylib";
            yield return "libprojectM-4-playlist.dylib";
        }
        else if (OperatingSystem.IsAndroid())
        {
            yield return "libprojectM-4-playlist.so";
        }
        else
        {
            yield return "libprojectM-4-playlist.so.4";
            yield return "libprojectM-4-playlist.so";
        }
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000:Assembly.Location returns an empty string for assemblies embedded in a single-file app",
        Justification = "The empty-string result is explicitly handled below; in a single-file/published app AppContext.BaseDirectory already covers native resolution.")]
    private static IEnumerable<string> ProbeDirectories()
    {
        var baseDirectory = AppContext.BaseDirectory;
        yield return baseDirectory;
        yield return Path.Combine(baseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");

        var assemblyDirectory = Path.GetDirectoryName(typeof(NativeLoader).Assembly.Location);
        if (!string.IsNullOrEmpty(assemblyDirectory) && assemblyDirectory != baseDirectory)
        {
            yield return assemblyDirectory;
            // NuGet package layout relative to lib/{tfm}/ProjectMDotNet.dll.
            yield return Path.GetFullPath(Path.Combine(
                assemblyDirectory, "..", "..", "runtimes", RuntimeInformation.RuntimeIdentifier, "native"));
        }
    }
}
