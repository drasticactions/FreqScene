using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProjectMDotNet.Interop;

namespace ProjectMDotNet;

/// <summary>
/// A projectM visualizer instance.
/// </summary>
public sealed unsafe class ProjectM : IDisposable
{
    private static readonly List<GCHandle> RootedLoadProcs = [];

    private projectm* _handle;
    private GCHandle _self;
    private readonly int _glThreadId;
    private bool _playlistAttached;
    private EventHandler<PresetSwitchRequestedEventArgs>? _presetSwitchRequested;
    private EventHandler<PresetSwitchFailedEventArgs>? _presetSwitchFailed;

    private ProjectM(projectm* handle)
    {
        _handle = handle;
        _glThreadId = Environment.CurrentManagedThreadId;
        _self = GCHandle.Alloc(this);
    }

    internal Action<Action>? GlWorkDispatcher { get; set; }

    internal bool InGlScope { get; set; }

    internal bool TryDispatchGlWork(Action work)
    {
        if (GlWorkDispatcher is { } dispatcher && !InGlScope)
        {
            dispatcher(work);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Version of the bundled native libprojectM.
    /// </summary>
    public static Version NativeVersion
    {
        get
        {
            int major, minor, patch;
            NativeMethods.projectm_get_version_components(&major, &minor, &patch);
            return new Version(major, minor, patch);
        }
    }

    /// <summary>
    /// Maximum number of samples per channel accepted by a single <see cref="AddPcm(ReadOnlySpan{float}, AudioChannels)"/> call.
    /// </summary>
    public static uint MaxPcmSamples => NativeMethods.projectm_pcm_get_max_samples();

    public static ProjectM Create(Func<string, IntPtr>? openGlLoadProc = null)
    {
        projectm* handle;
        if (openGlLoadProc is null)
        {
            handle = NativeMethods.projectm_create();
        }
        else
        {
            var rooted = GCHandle.Alloc(openGlLoadProc);
            lock (RootedLoadProcs)
            {
                RootedLoadProcs.Add(rooted);
            }

            handle = NativeMethods.projectm_create_with_opengl_load_proc(
                &LoadProcThunk, (void*)GCHandle.ToIntPtr(rooted));
        }

        if (handle is null)
        {
            throw new ProjectMException(
                "projectm_create failed. Ensure an OpenGL 3.3 Core (or OpenGL ES 3) context is current on this thread.");
        }

        return new ProjectM(handle);
    }

    internal projectm* Handle
    {
        get
        {
            ThrowIfDisposed();
            return _handle;
        }
    }

    /// <summary>
    /// Raised when projectM requests a preset switch. Unavailable while a <see cref="ProjectMPlaylist"/> is attached.
    /// </summary>
    public event EventHandler<PresetSwitchRequestedEventArgs>? PresetSwitchRequested
    {
        add
        {
            ThrowIfPlaylistAttached();
            var register = _presetSwitchRequested is null;
            _presetSwitchRequested += value;
            if (register)
            {
                NativeMethods.projectm_set_preset_switch_requested_event_callback(
                    Handle, &OnPresetSwitchRequested, (void*)GCHandle.ToIntPtr(_self));
            }
        }
        remove
        {
            _presetSwitchRequested -= value;
            if (_presetSwitchRequested is null && _handle is not null && !_playlistAttached)
            {
                NativeMethods.projectm_set_preset_switch_requested_event_callback(_handle, null, null);
            }
        }
    }

    /// <summary>
    /// Raised when a preset failed to load. Unavailable while a <see cref="ProjectMPlaylist"/> is attached.
    /// </summary>
    public event EventHandler<PresetSwitchFailedEventArgs>? PresetSwitchFailed
    {
        add
        {
            ThrowIfPlaylistAttached();
            var register = _presetSwitchFailed is null;
            _presetSwitchFailed += value;
            if (register)
            {
                NativeMethods.projectm_set_preset_switch_failed_event_callback(
                    Handle, &OnPresetSwitchFailed, (void*)GCHandle.ToIntPtr(_self));
            }
        }
        remove
        {
            _presetSwitchFailed -= value;
            if (_presetSwitchFailed is null && _handle is not null && !_playlistAttached)
            {
                NativeMethods.projectm_set_preset_switch_failed_event_callback(_handle, null, null);
            }
        }
    }

    public void LoadPresetFile(string path, bool smoothTransition)
    {
        if (TryDispatchGlWork(() => LoadPresetFile(path, smoothTransition)))
        {
            return;
        }

        AssertGlThread();
        NativeStrings.WithUtf8(path, p =>
            NativeMethods.projectm_load_preset_file(Handle, (sbyte*)p, (byte)(smoothTransition ? 1 : 0)));
    }

    /// <summary>Loads a Milkdrop preset from preset text. Deferred like <see cref="LoadPresetFile"/> when hosted.</summary>
    public void LoadPresetData(string milkdropPresetData, bool smoothTransition)
    {
        if (TryDispatchGlWork(() => LoadPresetData(milkdropPresetData, smoothTransition)))
        {
            return;
        }

        AssertGlThread();
        NativeStrings.WithUtf8(milkdropPresetData, p =>
            NativeMethods.projectm_load_preset_data(Handle, (sbyte*)p, (byte)(smoothTransition ? 1 : 0)));
    }

    /// <summary>
    /// Renders a frame into the currently bound framebuffer.
    /// </summary>
    public void RenderFrame()
    {
        AssertGlThread();
        NativeMethods.projectm_opengl_render_frame(Handle);
    }

    /// <summary>
    /// Renders a frame into the given framebuffer object.
    /// </summary>
    public void RenderFrame(uint framebufferObjectId)
    {
        AssertGlThread();
        NativeMethods.projectm_opengl_render_frame_fbo(Handle, framebufferObjectId);
    }

    /// <summary>
    /// Reloads all textures and rebuilds render targets. Deferred like <see cref="LoadPresetFile"/> when hosted.
    /// </summary>
    public void ResetTextures()
    {
        if (TryDispatchGlWork(ResetTextures))
        {
            return;
        }

        NativeMethods.projectm_reset_textures(Handle);
    }

    /// <summary>
    /// Adds interleaved 32-bit float PCM data (range −1..1).
    /// </summary>
    public void AddPcm(ReadOnlySpan<float> interleavedSamples, AudioChannels channels)
    {
        fixed (float* samples = interleavedSamples)
        {
            NativeMethods.projectm_pcm_add_float(
                Handle, samples, SamplesPerChannel(interleavedSamples.Length, channels), (projectm_channels)channels);
        }
    }

    /// <summary>
    /// Adds interleaved 16-bit signed integer PCM data.
    /// </summary>
    public void AddPcm(ReadOnlySpan<short> interleavedSamples, AudioChannels channels)
    {
        fixed (short* samples = interleavedSamples)
        {
            NativeMethods.projectm_pcm_add_int16(
                Handle, samples, SamplesPerChannel(interleavedSamples.Length, channels), (projectm_channels)channels);
        }
    }

    /// <summary>
    /// Adds interleaved 8-bit unsigned integer PCM data.
    /// </summary>
    public void AddPcm(ReadOnlySpan<byte> interleavedSamples, AudioChannels channels)
    {
        fixed (byte* samples = interleavedSamples)
        {
            NativeMethods.projectm_pcm_add_uint8(
                Handle, samples, SamplesPerChannel(interleavedSamples.Length, channels), (projectm_channels)channels);
        }
    }

    /// <summary>
    /// Beat detection sensitivity. Noop above 4.2.0.
    /// </summary>
    public float BeatSensitivity
    {
        get => NativeMethods.projectm_get_beat_sensitivity(Handle);
        set => NativeMethods.projectm_set_beat_sensitivity(Handle, value);
    }

    /// <summary>
    /// Minimum seconds a preset plays before a beat-driven hard cut can occur.
    /// </summary>
    public double HardCutDuration
    {
        get => NativeMethods.projectm_get_hard_cut_duration(Handle);
        set => NativeMethods.projectm_set_hard_cut_duration(Handle, value);
    }

    /// <summary>
    /// Whether beat-driven hard cuts are enabled.
    /// </summary>
    public bool HardCutEnabled
    {
        get => NativeMethods.projectm_get_hard_cut_enabled(Handle) != 0;
        set => NativeMethods.projectm_set_hard_cut_enabled(Handle, (byte)(value ? 1 : 0));
    }

    /// <summary>
    /// Beat sensitivity threshold for hard cuts.
    /// </summary>
    public float HardCutSensitivity
    {
        get => NativeMethods.projectm_get_hard_cut_sensitivity(Handle);
        set => NativeMethods.projectm_set_hard_cut_sensitivity(Handle, value);
    }

    /// <summary>
    /// Seconds a soft-cut (blended) preset transition takes.
    /// </summary>
    public double SoftCutDuration
    {
        get => NativeMethods.projectm_get_soft_cut_duration(Handle);
        set => NativeMethods.projectm_set_soft_cut_duration(Handle, value);
    }

    /// <summary>
    /// Seconds each preset is displayed before an automatic switch.
    /// </summary>
    public double PresetDuration
    {
        get => NativeMethods.projectm_get_preset_duration(Handle);
        set => NativeMethods.projectm_set_preset_duration(Handle, value);
    }

    /// <summary>
    /// Per-preset mesh (grid) resolution used for warp effects.
    /// </summary>
    public (int Width, int Height) MeshSize
    {
        get
        {
            nuint width, height;
            NativeMethods.projectm_get_mesh_size(Handle, &width, &height);
            return ((int)width, (int)height);
        }
        set => NativeMethods.projectm_set_mesh_size(Handle, (nuint)value.Width, (nuint)value.Height);
    }

    /// <summary>
    /// Target frames per second communicated to presets (used by preset math, not a frame limiter).
    /// </summary>
    public int TargetFps
    {
        get => NativeMethods.projectm_get_fps(Handle);
        set => NativeMethods.projectm_set_fps(Handle, value);
    }

    /// <summary>
    /// Whether aspect-ratio correction is applied for non-square windows.
    /// </summary>
    public bool AspectCorrection
    {
        get => NativeMethods.projectm_get_aspect_correction(Handle) != 0;
        set => NativeMethods.projectm_set_aspect_correction(Handle, (byte)(value ? 1 : 0));
    }

    /// <summary>
    /// Mean/variance of the random preset-duration "easter egg" jitter.
    /// </summary>
    public float EasterEgg
    {
        get => NativeMethods.projectm_get_easter_egg(Handle);
        set => NativeMethods.projectm_set_easter_egg(Handle, value);
    }

    /// <summary>
    /// When true, automatic preset switching is disabled.
    /// </summary>
    public bool PresetLocked
    {
        get => NativeMethods.projectm_get_preset_locked(Handle) != 0;
        set => NativeMethods.projectm_set_preset_locked(Handle, (byte)(value ? 1 : 0));
    }

    /// <summary>
    /// Rendering viewport size in pixels. Must be non-zero for anything to render.
    /// </summary>
    public (int Width, int Height) WindowSize
    {
        get
        {
            nuint width, height;
            NativeMethods.projectm_get_window_size(Handle, &width, &height);
            return ((int)width, (int)height);
        }
        set => NativeMethods.projectm_set_window_size(Handle, (nuint)value.Width, (nuint)value.Height);
    }

    /// <summary>
    /// When true, presets start with a cleared (black) framebuffer instead of the previous frame.
    /// </summary>
    public bool PresetStartClean
    {
        get => NativeMethods.projectm_get_preset_start_clean(Handle) != 0;
        set => NativeMethods.projectm_set_preset_start_clean(Handle, (byte)(value ? 1 : 0));
    }

    /// <summary>
    /// Texel offset applied when sampling the previous frame.
    /// </summary>
    public (float X, float Y) TexelOffset
    {
        get
        {
            float x, y;
            NativeMethods.projectm_get_texel_offset(Handle, &x, &y);
            return (x, y);
        }
        set => NativeMethods.projectm_set_texel_offset(Handle, value.X, value.Y);
    }

    /// <summary>
    /// Seconds elapsed since the first frame, as used by the last rendered frame.
    /// </summary>
    public double LastFrameTime => NativeMethods.projectm_get_last_frame_time(Handle);

    /// <summary>
    /// Overrides the time used for the next frame.
    /// </summary>
    public void SetFrameTime(double secondsSinceFirstFrame) =>
        NativeMethods.projectm_set_frame_time(Handle, secondsSinceFirstFrame);

    /// <summary>
    /// Sets the directories searched for preset textures.
    /// </summary>
    public void SetTextureSearchPaths(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        NativeStrings.WithUtf8Array(paths, (array, count) =>
            NativeMethods.projectm_set_texture_search_paths(Handle, (sbyte**)array, count));
    }

    /// <summary>
    /// Starts a touch waveform at the given normalized coordinates.
    /// </summary>
    public void Touch(float x, float y, int pressure, TouchType touchType) =>
        NativeMethods.projectm_touch(Handle, x, y, pressure, (projectm_touch_type)touchType);

    /// <summary>
    /// Moves an active touch waveform.
    /// </summary>
    public void TouchDrag(float x, float y, int pressure) =>
        NativeMethods.projectm_touch_drag(Handle, x, y, pressure);

    /// <summary>
    /// Removes the touch waveform nearest to the given coordinates.
    /// </summary>
    public void TouchDestroy(float x, float y) =>
        NativeMethods.projectm_touch_destroy(Handle, x, y);

    /// <summary>
    /// Removes all touch waveforms.
    /// </summary>
    public void TouchDestroyAll() => NativeMethods.projectm_touch_destroy_all(Handle);

    /// <summary>
    /// Writes the next rendered frame to an image file or an auto-generated filename when null.
    /// </summary>
    public void WriteDebugImageOnNextFrame(string? outputFile)
    {
        if (outputFile is null)
        {
            NativeMethods.projectm_write_debug_image_on_next_frame(Handle, null);
        }
        else
        {
            NativeStrings.WithUtf8(outputFile, p =>
                NativeMethods.projectm_write_debug_image_on_next_frame(Handle, (sbyte*)p));
        }
    }

    /// <summary>
    /// Destroys the native instance. Must be called on the GL thread with the
    /// same context current that was used at creation time.
    /// </summary>
    public void Dispose()
    {
        if (_handle is null)
        {
            return;
        }

        if (!_playlistAttached)
        {
            NativeMethods.projectm_set_preset_switch_requested_event_callback(_handle, null, null);
            NativeMethods.projectm_set_preset_switch_failed_event_callback(_handle, null, null);
        }

        NativeMethods.projectm_destroy(_handle);
        _handle = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    internal void Abandon()
    {
        _handle = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    internal void AttachPlaylist()
    {
        ThrowIfDisposed();
        if (_playlistAttached)
        {
            throw new InvalidOperationException("A ProjectMPlaylist is already attached to this ProjectM instance.");
        }

        if (_presetSwitchRequested is not null || _presetSwitchFailed is not null)
        {
            throw new InvalidOperationException(
                "Cannot attach a playlist while PresetSwitchRequested/PresetSwitchFailed handlers are subscribed: the native playlist takes ownership of those callbacks.");
        }

        _playlistAttached = true;
    }

    internal void DetachPlaylist() => _playlistAttached = false;

    private void ThrowIfPlaylistAttached()
    {
        if (_playlistAttached)
        {
            throw new InvalidOperationException(
                "This event is owned by the attached ProjectMPlaylist; subscribe to the playlist's events instead.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_handle is null, this);

    [Conditional("DEBUG")]
    private void AssertGlThread() =>
        Debug.Assert(
            Environment.CurrentManagedThreadId == _glThreadId,
            "ProjectM members must be called on the thread that owns the OpenGL context.");

    private static uint SamplesPerChannel(int totalSamples, AudioChannels channels) =>
        (uint)(channels == AudioChannels.Stereo ? totalSamples / 2 : totalSamples);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* LoadProcThunk(sbyte* name, void* userData)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userData).Target is Func<string, IntPtr> resolver)
            {
                var procName = Marshal.PtrToStringUTF8((IntPtr)name) ?? string.Empty;
                return (void*)resolver(procName);
            }
        }
        catch
        {
            // Exceptions must not cross the native boundary.
        }

        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPresetSwitchRequested(byte isHardCut, void* userData)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userData).Target is ProjectM instance)
            {
                instance._presetSwitchRequested?.Invoke(instance, new PresetSwitchRequestedEventArgs(isHardCut != 0));
            }
        }
        catch
        {
            // Exceptions must not cross the native boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPresetSwitchFailed(sbyte* presetFilename, sbyte* message, void* userData)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userData).Target is ProjectM instance)
            {
                var filename = Marshal.PtrToStringUTF8((IntPtr)presetFilename) ?? string.Empty;
                var error = Marshal.PtrToStringUTF8((IntPtr)message) ?? string.Empty;
                instance._presetSwitchFailed?.Invoke(instance, new PresetSwitchFailedEventArgs(filename, error));
            }
        }
        catch
        {
            // Exceptions must not cross the native boundary.
        }
    }
}
