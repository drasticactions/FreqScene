using System.Diagnostics;
using Avalonia.Threading;
using ProjectMDotNet;

namespace FreqScene;

internal sealed class MacVisualizerHost : IVisualizerHost
{
    private const int PfaDoubleBuffer = 5;
    private const int PfaColorSize = 8;
    private const int PfaAlphaSize = 11;
    private const int PfaDepthSize = 12;
    private const int PfaAccelerated = 73;
    private const int PfaOpenGlProfile = 99;
    private const int ProfileVersion32Core = 0x3200;
    private const long ParameterSwapInterval = 222;
    private const long ParameterSurfaceOpacity = 236;

    private readonly IntPtr _window;
    private readonly IntPtr _view;
    private readonly bool _transparent;
    private readonly PcmBuffer _pcmBuffer = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _glActions = new();
    private readonly GlFramePipeline _pipeline = new();

    private static readonly IntPtr SelBounds = MacInterop.Sel("bounds");
    private static readonly IntPtr SelFrame = MacInterop.Sel("frame");
    private static readonly IntPtr SelBackingScaleFactor = MacInterop.Sel("backingScaleFactor");
    private static readonly IntPtr SelWindow = MacInterop.Sel("window");
    private static readonly IntPtr SelScreen = MacInterop.Sel("screen");
    private static readonly IntPtr SelMaximumFramesPerSecond = MacInterop.Sel("maximumFramesPerSecond");
    private static readonly IntPtr SelAlloc = MacInterop.Sel("alloc");
    private static readonly IntPtr SelRelease = MacInterop.Sel("release");
    private static readonly IntPtr SelInitWithAttributes = MacInterop.Sel("initWithAttributes:");
    private static readonly IntPtr SelInitWithFormatShareContext = MacInterop.Sel("initWithFormat:shareContext:");
    private static readonly IntPtr SelSetView = MacInterop.Sel("setView:");
    private static readonly IntPtr SelUpdate = MacInterop.Sel("update");
    private static readonly IntPtr SelMakeCurrentContext = MacInterop.Sel("makeCurrentContext");
    private static readonly IntPtr SelFlushBuffer = MacInterop.Sel("flushBuffer");
    private static readonly IntPtr SelClearDrawable = MacInterop.Sel("clearDrawable");
    private static readonly IntPtr SelSetValuesForParameter = MacInterop.Sel("setValues:forParameter:");

    private IntPtr _openGlLibrary;
    private IntPtr _pixelFormat;
    private IntPtr _context;
    private ProjectM? _instance;
    private ProjectMPlaylist? _playlist;
    private IReadOnlyList<string> _textureSearchPaths = [];
    private double _presetDuration = 30.0;
    private bool _presetLocked;
    private double _maxFrameRate;
    private double _renderScale = 1.0;
    private bool _viewAttached;
    private bool _started;
    private bool _disposed;
    private bool _failed;
    private long _nextFrameDue;
    private MacInterop.CgRect _lastFrame;
    private double _lastBackingScale;

    public MacVisualizerHost(IntPtr window, IntPtr view, bool transparent)
    {
        _window = window;
        _view = view;
        _transparent = transparent;
    }

    public ProjectM? Instance => _instance;

    public ProjectMPlaylist? Playlist => _playlist;

    public event EventHandler? InstanceCreated;

    public event EventHandler<Exception>? InitializationFailed;

    public double PresetDuration
    {
        get => _presetDuration;
        set
        {
            _presetDuration = value;
            if (_instance is { } instance)
            {
                instance.PresetDuration = value;
            }
        }
    }

    public bool PresetLocked
    {
        get => _presetLocked;
        set
        {
            _presetLocked = value;
            if (_instance is { } instance)
            {
                instance.PresetLocked = value;
            }
        }
    }

    public double MaxFrameRate
    {
        get => _maxFrameRate;
        set
        {
            _maxFrameRate = value;
            _nextFrameDue = 0;
        }
    }

    public double RenderScale
    {
        get => _renderScale;
        set => _renderScale = double.IsFinite(value) ? Math.Clamp(value, 0.05, 1.0) : 1.0;
    }

    public void AddPcm(ReadOnlySpan<float> interleavedSamples, AudioChannels channels) =>
        _pcmBuffer.Add(interleavedSamples, channels);

    public void AddPcm(ReadOnlySpan<short> interleavedSamples, AudioChannels channels) =>
        _pcmBuffer.Add(interleavedSamples, channels);

    public ProjectMPlaylist EnablePlaylist()
    {
        if (_playlist is not null)
        {
            return _playlist;
        }

        if (_instance is null)
        {
            throw new InvalidOperationException(
                "The visualizer is not initialized yet; call EnablePlaylist from the InstanceCreated event or later.");
        }

        _playlist = new ProjectMPlaylist(_instance);
        return _playlist;
    }

    public void ApplyTextureSearchPaths(IReadOnlyList<string> paths)
    {
        _textureSearchPaths = paths ?? [];
        if (_instance is { } instance)
        {
            RunWithGlContext(() => instance.SetTextureSearchPaths(_textureSearchPaths));
        }
    }

    /// <summary>Queues an action to run before the next frame with the GL context current.</summary>
    public void RunWithGlContext(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _glActions.Enqueue(action);
    }

    /// <summary>Creates the GL context and starts the frame loop. Call after the window is on screen.</summary>
    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        try
        {
            _openGlLibrary = MacInterop.DlOpen("/System/Library/Frameworks/OpenGL.framework/OpenGL", 2);
            if (_openGlLibrary == IntPtr.Zero)
            {
                throw new InvalidOperationException("OpenGL.framework could not be loaded.");
            }

            var library = _openGlLibrary;
            Gl.Initialize(name => MacInterop.DlSym(library, name));

            uint[] attributes =
            [
                PfaOpenGlProfile, ProfileVersion32Core,
                PfaColorSize, 24,
                PfaAlphaSize, 8,
                PfaDepthSize, 24,
                PfaDoubleBuffer,
                PfaAccelerated,
                0,
            ];
            var pixelFormatClass = MacInterop.GetClass("NSOpenGLPixelFormat");
            _pixelFormat = MacInterop.MsgSendInitAttributes(
                MacInterop.MsgSend(pixelFormatClass, SelAlloc), SelInitWithAttributes, attributes);
            if (_pixelFormat == IntPtr.Zero)
            {
                throw new InvalidOperationException("No OpenGL 3.2+ core pixel format is available.");
            }

            var contextClass = MacInterop.GetClass("NSOpenGLContext");
            _context = MacInterop.MsgSend(
                MacInterop.MsgSend(contextClass, SelAlloc), SelInitWithFormatShareContext, _pixelFormat, IntPtr.Zero);
            if (_context == IntPtr.Zero)
            {
                throw new InvalidOperationException("NSOpenGLContext creation failed.");
            }

            // Never block the UI thread on vsync; frame pacing is ours.
            var swapInterval = 0;
            MacInterop.MsgSendSetValues(_context, SelSetValuesForParameter, ref swapInterval, ParameterSwapInterval);
            if (_transparent)
            {
                var opacity = 0;
                MacInterop.MsgSendSetValues(_context, SelSetValuesForParameter, ref opacity, ParameterSurfaceOpacity);
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            InitializationFailed?.Invoke(this, ex);
            return;
        }

        RenderFrame();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _glActions.Clear();
        _pcmBuffer.Clear();

        if (_context != IntPtr.Zero)
        {
            MacInterop.MsgSendVoid(_context, SelMakeCurrentContext);
            if (_instance is { } instance)
            {
                instance.InGlScope = true;
            }

            _playlist?.Dispose();
            _playlist = null;
            _instance?.Dispose();
            _instance = null;
            _pipeline.Release();
            MacInterop.MsgSendVoid(_context, SelClearDrawable);
            MacInterop.MsgSendVoid(_context, SelRelease);
            _context = IntPtr.Zero;
        }

        if (_pixelFormat != IntPtr.Zero)
        {
            MacInterop.MsgSendVoid(_pixelFormat, SelRelease);
            _pixelFormat = IntPtr.Zero;
        }
    }

    private void RenderFrame()
    {
        if (_disposed || _failed)
        {
            return;
        }

        try
        {
            RenderCore();
        }
        catch (Exception ex)
        {
            Trace.TraceError($"MacVisualizerHost frame failed: {ex}");
        }

        ScheduleNextFrame();
    }

    private void RenderCore()
    {
        if (!_viewAttached)
        {
            if (MacInterop.MsgSend(_view, SelWindow) == IntPtr.Zero)
            {
                return;
            }

            MacInterop.MsgSendVoid(_context, SelSetView, _view);
            _viewAttached = true;
        }

        MacInterop.MsgSendVoid(_context, SelMakeCurrentContext);
        var (width, height) = UpdateGeometry();
        if (width <= 0 || height <= 0)
        {
            return;
        }

        EnsureInstance();
        if (_instance is not { } instance)
        {
            return;
        }

        instance.InGlScope = true;
        try
        {
            while (_glActions.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"MacVisualizerHost GL action failed: {ex}");
                }
            }

            _pcmBuffer.Drain(instance);
            _pipeline.Render(instance, width, height, _renderScale, _transparent);
        }
        finally
        {
            instance.InGlScope = false;
        }

        MacInterop.MsgSendVoid(_context, SelFlushBuffer);
    }

    private (int Width, int Height) UpdateGeometry()
    {
        var frame = MacInterop.MsgSendRect(_window, SelFrame);
        var backingScale = MacInterop.MsgSendDouble(_window, SelBackingScaleFactor);
        if (frame.Origin.X != _lastFrame.Origin.X || frame.Origin.Y != _lastFrame.Origin.Y ||
            frame.Size.Width != _lastFrame.Size.Width || frame.Size.Height != _lastFrame.Size.Height ||
            backingScale != _lastBackingScale)
        {
            MacInterop.MsgSendVoid(_context, SelUpdate);
            _lastFrame = frame;
            _lastBackingScale = backingScale;
        }

        var bounds = MacInterop.MsgSendRect(_view, SelBounds);
        return ((int)Math.Round(bounds.Size.Width * backingScale), (int)Math.Round(bounds.Size.Height * backingScale));
    }

    private void EnsureInstance()
    {
        if (_instance is not null || _failed)
        {
            return;
        }

        try
        {
            var library = _openGlLibrary;
            var instance = ProjectM.Create(name => MacInterop.DlSym(library, name));
            _instance = instance;
            instance.GlWorkDispatcher = RunWithGlContext;
            instance.InGlScope = true;
            try
            {
                _pipeline.ResetWindowSize();
                instance.PresetDuration = _presetDuration;
                instance.PresetLocked = _presetLocked;
                instance.AspectCorrection = true;
                if (_textureSearchPaths.Count > 0)
                {
                    instance.SetTextureSearchPaths(_textureSearchPaths);
                }

                instance.LoadPresetFile("idle://", smoothTransition: false);
                InstanceCreated?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                instance.InGlScope = false;
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            _instance?.Dispose();
            _instance = null;
            InitializationFailed?.Invoke(this, ex);
        }
    }

    private void ScheduleNextFrame()
    {
        if (_disposed || _failed)
        {
            return;
        }

        var maxFrameRate = _maxFrameRate > 0 ? _maxFrameRate : DisplayFrameRate();
        var now = Stopwatch.GetTimestamp();
        var interval = (long)(Stopwatch.Frequency / maxFrameRate);
        _nextFrameDue = Math.Max(_nextFrameDue + interval, now);

        var delay = TimeSpan.FromSeconds((_nextFrameDue - now) / (double)Stopwatch.Frequency);
        if (delay < TimeSpan.FromMilliseconds(1))
        {
            // Never re-enter immediately: when a heavy preset runs the frame past
            // its budget, an immediate high-priority repost would starve input and
            // hang the UI. A minimum delay at sub-Input priority lets the
            // dispatcher drain pending events between frames.
            delay = TimeSpan.FromMilliseconds(1);
        }

        DispatcherTimer.RunOnce(RenderFrame, delay, DispatcherPriority.Background);
    }

    private double DisplayFrameRate()
    {
        if (OperatingSystem.IsMacOSVersionAtLeast(12))
        {
            var screen = MacInterop.MsgSend(_window, SelScreen);
            if (screen != IntPtr.Zero)
            {
                var rate = MacInterop.MsgSendLong(screen, SelMaximumFramesPerSecond);
                if (rate > 0)
                {
                    return rate;
                }
            }
        }

        return 60;
    }
}
