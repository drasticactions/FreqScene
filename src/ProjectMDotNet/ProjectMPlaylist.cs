using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ProjectMDotNet.Interop;

namespace ProjectMDotNet;

public sealed unsafe class ProjectMPlaylist : IDisposable
{
    private readonly ProjectM _core;
    private projectm_playlist* _handle;
    private GCHandle _self;

    public ProjectMPlaylist(ProjectM instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        instance.AttachPlaylist();
        try
        {
            _handle = PlaylistNativeMethods.projectm_playlist_create(instance.Handle);
            if (_handle is null)
            {
                throw new ProjectMException("projectm_playlist_create failed.");
            }
        }
        catch
        {
            instance.DetachPlaylist();
            throw;
        }

        _core = instance;
        _self = GCHandle.Alloc(this);
        PlaylistNativeMethods.projectm_playlist_set_preset_switched_event_callback(
            _handle, &OnPresetSwitched, (void*)GCHandle.ToIntPtr(_self));
        PlaylistNativeMethods.projectm_playlist_set_preset_switch_failed_event_callback(
            _handle, &OnPresetSwitchFailed, (void*)GCHandle.ToIntPtr(_self));
    }

    /// <summary>
    /// The <see cref="ProjectM"/> instance this playlist controls.
    /// </summary>
    public ProjectM Instance => _core;

    /// <summary>
    /// Raised after the playlist switched to another preset.
    /// </summary>
    public event EventHandler<PresetSwitchedEventArgs>? PresetSwitched;

    /// <summary>
    /// Raised when a preset failed to load.
    /// </summary>
    public event EventHandler<PresetSwitchFailedEventArgs>? PresetSwitchFailed;

    private EventHandler<PresetLoadingEventArgs>? _presetLoading;

    /// <summary>
    /// Raised before a preset loads; set <see cref="PresetLoadingEventArgs.Cancel"/>
    /// to true to suppress the load.
    /// </summary>
    public event EventHandler<PresetLoadingEventArgs>? PresetLoading
    {
        add
        {
            var register = _presetLoading is null;
            _presetLoading += value;
            if (register)
            {
                PlaylistNativeMethods.projectm_playlist_set_preset_load_event_callback(
                    Handle, &OnPresetLoading, (void*)GCHandle.ToIntPtr(_self));
            }
        }
        remove
        {
            _presetLoading -= value;
            if (_presetLoading is null && _handle is not null)
            {
                PlaylistNativeMethods.projectm_playlist_set_preset_load_event_callback(_handle, null, null);
            }
        }
    }

    /// <summary>
    /// Number of items in the playlist.
    /// </summary>
    public uint Count => PlaylistNativeMethods.projectm_playlist_size(Handle);

    /// <summary>
    /// Whether presets play in random order.
    /// </summary>
    public bool Shuffle
    {
        get => PlaylistNativeMethods.projectm_playlist_get_shuffle(Handle) != 0;
        set => PlaylistNativeMethods.projectm_playlist_set_shuffle(Handle, (byte)(value ? 1 : 0));
    }

    /// <summary>
    /// Number of automatic retries after failed preset switches. Defaults to 500.
    /// </summary>
    public uint RetryCount
    {
        get => PlaylistNativeMethods.projectm_playlist_get_retry_count(Handle);
        set => PlaylistNativeMethods.projectm_playlist_set_retry_count(Handle, value);
    }

    /// <summary>
    /// Current playlist position.
    /// </summary>
    public uint Position => PlaylistNativeMethods.projectm_playlist_get_position(Handle);

    /// <summary>
    /// Removes all items.
    /// </summary>
    public void Clear() => PlaylistNativeMethods.projectm_playlist_clear(Handle);

    /// <summary>
    /// Adds all <c>.milk</c> presets found under <paramref name="path"/>.
    /// Returns the number of presets added.
    /// </summary>
    public uint AddPath(string path, bool recurseSubdirectories = true, bool allowDuplicates = false) =>
        NativeStrings.WithUtf8(path, p => PlaylistNativeMethods.projectm_playlist_add_path(
            Handle, (sbyte*)p, (byte)(recurseSubdirectories ? 1 : 0), (byte)(allowDuplicates ? 1 : 0)));

    /// <summary>
    /// Inserts all <c>.milk</c> presets found under <paramref name="path"/> at the given index.
    /// </summary>
    public uint InsertPath(string path, uint index, bool recurseSubdirectories = true, bool allowDuplicates = false) =>
        NativeStrings.WithUtf8(path, p => PlaylistNativeMethods.projectm_playlist_insert_path(
            Handle, (sbyte*)p, index, (byte)(recurseSubdirectories ? 1 : 0), (byte)(allowDuplicates ? 1 : 0)));

    /// <summary>
    /// Appends a single preset file. Returns false if it was a rejected duplicate.
    /// </summary>
    public bool AddPreset(string filename, bool allowDuplicates = false) =>
        NativeStrings.WithUtf8(filename, p => PlaylistNativeMethods.projectm_playlist_add_preset(
            Handle, (sbyte*)p, (byte)(allowDuplicates ? 1 : 0))) != 0;

    /// <summary>
    /// Inserts a single preset file at the given index. Returns false if it was a rejected duplicate.
    /// </summary>
    public bool InsertPreset(string filename, uint index, bool allowDuplicates = false) =>
        NativeStrings.WithUtf8(filename, p => PlaylistNativeMethods.projectm_playlist_insert_preset(
            Handle, (sbyte*)p, index, (byte)(allowDuplicates ? 1 : 0))) != 0;

    /// <summary>Removes the preset at <paramref name="index"/>.</summary>
    public bool RemovePreset(uint index) =>
        PlaylistNativeMethods.projectm_playlist_remove_preset(Handle, index) != 0;

    /// <summary>
    /// Removes <paramref name="count"/> presets starting at <paramref name="index"/>. Returns the number removed.
    /// </summary>
    public uint RemovePresets(uint index, uint count) =>
        PlaylistNativeMethods.projectm_playlist_remove_presets(Handle, index, count);

    /// <summary>
    /// Returns the preset filename at <paramref name="index"/>, or null if out of range.
    /// </summary>
    public string? GetItem(uint index) =>
        NativeStrings.ConsumePlaylistString(PlaylistNativeMethods.projectm_playlist_item(Handle, index));

    /// <summary>
    /// Returns playlist items, optionally windowed by <paramref name="start"/>
    /// and <paramref name="count"/> (the maximum number of items returned).
    /// </summary>
    public IReadOnlyList<string> GetItems(uint start = 0, uint count = uint.MaxValue) =>
        NativeStrings.ConsumePlaylistStringArray(
            PlaylistNativeMethods.projectm_playlist_items(Handle, start, count));

    /// <summary>
    /// Sorts a range of the playlist.
    /// </summary>
    public void Sort(uint startIndex, uint count, PlaylistSortPredicate predicate, PlaylistSortOrder order) =>
        PlaylistNativeMethods.projectm_playlist_sort(
            Handle, startIndex, count,
            (projectm_playlist_sort_predicate)predicate, (projectm_playlist_sort_order)order);

    /// <summary>
    /// Sorts the whole playlist.
    /// </summary>
    public void Sort(PlaylistSortPredicate predicate = PlaylistSortPredicate.FullPath, PlaylistSortOrder order = PlaylistSortOrder.Ascending) =>
        Sort(0, Count, predicate, order);

    /// <summary>
    /// Switches to the preset at <paramref name="index"/>.
    /// </summary>
    public uint SetPosition(uint index, bool hardCut = false) =>
        _core.TryDispatchGlWork(() => SetPosition(index, hardCut))
            ? Position
            : PlaylistNativeMethods.projectm_playlist_set_position(Handle, index, (byte)(hardCut ? 1 : 0));

    /// <summary>
    /// Plays the next preset. Returns the new position (deferred like <see cref="SetPosition"/> when hosted).
    /// </summary>
    public uint PlayNext(bool hardCut = false) =>
        _core.TryDispatchGlWork(() => PlayNext(hardCut))
            ? Position
            : PlaylistNativeMethods.projectm_playlist_play_next(Handle, (byte)(hardCut ? 1 : 0));

    /// <summary>
    /// Plays the previous preset. Returns the new position (deferred like <see cref="SetPosition"/> when hosted).
    /// </summary>
    public uint PlayPrevious(bool hardCut = false) =>
        _core.TryDispatchGlWork(() => PlayPrevious(hardCut))
            ? Position
            : PlaylistNativeMethods.projectm_playlist_play_previous(Handle, (byte)(hardCut ? 1 : 0));

    /// <summary>
    /// Replays the last preset from the play history. Returns the new position.
    /// </summary>
    public uint PlayLast(bool hardCut = false) =>
        _core.TryDispatchGlWork(() => PlayLast(hardCut))
            ? Position
            : PlaylistNativeMethods.projectm_playlist_play_last(Handle, (byte)(hardCut ? 1 : 0));

    /// <summary>
    /// Sets the filename filter list applied when adding paths.
    /// </summary>
    public void SetFilter(IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        NativeStrings.WithUtf8Array(patterns, (array, count) =>
            PlaylistNativeMethods.projectm_playlist_set_filter(Handle, (sbyte**)array, count));
    }

    /// <summary>
    /// Returns the current filter list.
    /// </summary>
    public IReadOnlyList<string> GetFilter()
    {
        nuint count;
        var array = PlaylistNativeMethods.projectm_playlist_get_filter(Handle, &count);
        return NativeStrings.ConsumePlaylistStringArray(array, count);
    }

    /// <summary>
    /// Applies the filter list to the existing items. Returns the number of items removed.
    /// </summary>
    public nuint ApplyFilter() => PlaylistNativeMethods.projectm_playlist_apply_filter(Handle);

    /// <summary>
    /// Destroys the native playlist and re-enables the core instance's preset
    /// events. Must be called on the GL thread, before disposing the
    /// <see cref="ProjectM"/> instance.
    /// </summary>
    public void Dispose()
    {
        if (_handle is null)
        {
            return;
        }

        PlaylistNativeMethods.projectm_playlist_set_preset_switched_event_callback(_handle, null, null);
        PlaylistNativeMethods.projectm_playlist_set_preset_switch_failed_event_callback(_handle, null, null);
        PlaylistNativeMethods.projectm_playlist_set_preset_load_event_callback(_handle, null, null);
        PlaylistNativeMethods.projectm_playlist_destroy(_handle);
        _handle = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }

        _core.DetachPlaylist();
    }

    /// <summary>
    /// Releases managed state without touching the native playlist. See
    /// <see cref="ProjectM.Abandon"/>.
    /// </summary>
    internal void Abandon()
    {
        _handle = null;
        if (_self.IsAllocated)
        {
            _self.Free();
        }

        _core.DetachPlaylist();
    }

    private projectm_playlist* Handle
    {
        get
        {
            ObjectDisposedException.ThrowIf(_handle is null, this);
            return _handle;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPresetSwitched(byte isHardCut, uint index, void* userData)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userData).Target is ProjectMPlaylist playlist)
            {
                playlist.PresetSwitched?.Invoke(playlist, new PresetSwitchedEventArgs(isHardCut != 0, index));
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
            if (GCHandle.FromIntPtr((IntPtr)userData).Target is ProjectMPlaylist playlist)
            {
                var filename = Marshal.PtrToStringUTF8((IntPtr)presetFilename) ?? string.Empty;
                var error = Marshal.PtrToStringUTF8((IntPtr)message) ?? string.Empty;
                playlist.PresetSwitchFailed?.Invoke(playlist, new PresetSwitchFailedEventArgs(filename, error));
            }
        }
        catch
        {
            // Exceptions must not cross the native boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnPresetLoading(uint index, sbyte* filename, byte hardCut, void* userData)
    {
        try
        {
            if (GCHandle.FromIntPtr((IntPtr)userData).Target is ProjectMPlaylist playlist &&
                playlist._presetLoading is { } handler)
            {
                var args = new PresetLoadingEventArgs(
                    index, Marshal.PtrToStringUTF8((IntPtr)filename) ?? string.Empty, hardCut != 0);
                handler.Invoke(playlist, args);
                return (byte)(args.Cancel ? 1 : 0);
            }
        }
        catch
        {
            // Exceptions must not cross the native boundary.
        }

        return 0;
    }
}
