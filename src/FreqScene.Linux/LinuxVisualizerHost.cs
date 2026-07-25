using System.Diagnostics;
using Mesa.Egl;
using Mesa.Native;
using ProjectMDotNet;

namespace FreqScene;

public sealed unsafe class LinuxVisualizerHost : IVisualizerHost, IDisposable
{
    private readonly Func<ILinuxGlSession> _sessionFactory;
    private readonly IUiDispatcher _dispatcher;
    private readonly bool _transparent;
    private readonly PcmBuffer _pcmBuffer = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _glActions = new();
    private readonly GlFramePipeline _pipeline = new();
    private readonly ManualResetEvent _stopEvent = new(false);

    private Thread? _renderThread;
    private ILinuxGlSession? _session;
    private EglDisplay? _eglDisplay;
    private EglContext? _eglContext;
    private EglSurface? _eglSurface;
    private volatile ProjectM? _instance;
    private volatile ProjectMPlaylist? _playlist;
    private IReadOnlyList<string> _textureSearchPaths = [];
    private double _presetDuration = 30.0;
    private bool _presetLocked;
    private double _maxFrameRate;
    private double _renderScale = 1.0;
    private bool _started;
    private volatile bool _disposed;
    private volatile bool _failed;
    private long _nextFrameDue;

    public LinuxVisualizerHost(
        DisplayMode mode,
        bool wallpaperTransparency,
        string? displayKey,
        IUiDispatcher? dispatcher = null)
        : this(
            () => new LinuxWaylandSession(mode, displayKey),
            transparent: mode == DisplayMode.Overlay || (mode == DisplayMode.Wallpaper && wallpaperTransparency),
            dispatcher)
    {
    }

    public LinuxVisualizerHost(Func<ILinuxGlSession> sessionFactory, bool transparent, IUiDispatcher? dispatcher = null)
    {
        _sessionFactory = sessionFactory;
        _transparent = transparent;
        _dispatcher = dispatcher ?? InlineUiDispatcher.Instance;
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
                RunWithGlContext(() => instance.PresetDuration = value);
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
                RunWithGlContext(() => instance.PresetLocked = value);
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

        if (_instance is not { } instance)
        {
            throw new InvalidOperationException(
                "The visualizer is not initialized yet; call EnablePlaylist from the InstanceCreated event or later.");
        }

        var playlist = new ProjectMPlaylist(instance);
        _playlist = playlist;
        return playlist;
    }

    public void ApplyTextureSearchPaths(IReadOnlyList<string> paths)
    {
        _textureSearchPaths = paths ?? [];
        if (_instance is { } instance)
        {
            var snapshot = _textureSearchPaths;
            RunWithGlContext(() => instance.SetTextureSearchPaths(snapshot));
        }
    }

    public void RunWithGlContext(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _glActions.Enqueue(action);
    }

    public void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        _renderThread = new Thread(RenderLoop)
        {
            Name = "FreqScene Render",
            IsBackground = true,
        };
        _renderThread.Start();
    }

    public void RequestShow() => _session?.RequestShow();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopEvent.Set();
        if (_renderThread is { } thread && !thread.Join(TimeSpan.FromSeconds(5)))
        {
            Trace.TraceWarning("[native] the render thread did not stop in time; abandoning it.");
        }

        _renderThread = null;
        _playlist = null;
        _glActions.Clear();
        _pcmBuffer.Clear();
        _stopEvent.Dispose();
    }

    private void RenderLoop()
    {
        try
        {
            _session = _sessionFactory();
            CreateContext();
        }
        catch (Exception ex)
        {
            _failed = true;
            TeardownOnRenderThread();
            _dispatcher.Post(() =>
            {
                if (!_disposed)
                {
                    InitializationFailed?.Invoke(this, ex);
                }
            });
            return;
        }

        try
        {
            while (!_stopEvent.WaitOne(NextFrameDelayMs()))
            {
                try
                {
                    _session.PumpEvents();
                    if (_session.Closed)
                    {
                        Trace.TraceWarning("[native] the display session ended; rendering stops.");
                        break;
                    }

                    _session.ApplyPendingResize();
                    if (_session.Visible)
                    {
                        RenderCore();
                    }
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"LinuxVisualizerHost frame failed: {ex}");
                }
            }
        }
        finally
        {
            TeardownOnRenderThread();
        }
    }

    private void CreateContext()
    {
        var session = _session!;
        _eglDisplay = EglDisplay.GetPlatformDisplay(session.EglPlatform, session.NativeDisplayHandle);
        EglDisplay.BindApi(EglApi.OpenGl);

        var configs = _eglDisplay.ChooseConfigs(
        [
            Libegl.EGL_SURFACE_TYPE, Libegl.EGL_WINDOW_BIT,
            Libegl.EGL_RENDERABLE_TYPE, Libegl.EGL_OPENGL_BIT,
            Libegl.EGL_RED_SIZE, 8,
            Libegl.EGL_GREEN_SIZE, 8,
            Libegl.EGL_BLUE_SIZE, 8,
            Libegl.EGL_ALPHA_SIZE, 8,
            Libegl.EGL_DEPTH_SIZE, 24,
        ]);
        if (configs.Length == 0)
        {
            throw new InvalidOperationException("No usable EGL config is available.");
        }

        var config = configs[0];
        if (session.RequiredNativeVisualId is { } visualId)
        {
            // On the GBM platform the native visual is a gbm format; a config
            // that does not match the gbm surface's format cannot present.
            config = Array.Find(
                configs, c => c.GetAttribute(Libegl.EGL_NATIVE_VISUAL_ID) == visualId) ?? config;
        }

        _eglContext = _eglDisplay.CreateContext(config, attribs:
        [
            Libegl.EGL_CONTEXT_MAJOR_VERSION, 3,
            Libegl.EGL_CONTEXT_MINOR_VERSION, 3,
            Libegl.EGL_CONTEXT_OPENGL_PROFILE_MASK, Libegl.EGL_CONTEXT_OPENGL_CORE_PROFILE_BIT,
        ]);
        _eglSurface = _eglDisplay.CreateWindowSurface(config, session.NativeWindowHandle);
        _eglContext.MakeCurrent(_eglSurface);

        // Never block on vsync; frame pacing is ours. A driver that rejects
        // interval 0 costs only pacing, so it must not fail initialization.
        try
        {
            _eglDisplay.SwapInterval(0);
        }
        catch (EglException)
        {
        }

        Gl.Initialize(GetGlFunction);
    }

    private static IntPtr GetGlFunction(string name)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        IntPtr pointer;
        fixed (byte* namePtr = utf8)
        {
            pointer = (IntPtr)Libegl.eglGetProcAddress((sbyte*)namePtr);
        }

        if (pointer != IntPtr.Zero)
        {
            return pointer;
        }

        foreach (var library in (string[])["libOpenGL.so.0", "libGL.so.1"])
        {
            if (System.Runtime.InteropServices.NativeLibrary.TryLoad(library, out var handle) &&
                System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, name, out pointer))
            {
                return pointer;
            }
        }

        return IntPtr.Zero;
    }

    private void RenderCore()
    {
        var session = _session!;
        var width = session.PixelWidth;
        var height = session.PixelHeight;
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
                    Trace.TraceError($"LinuxVisualizerHost GL action failed: {ex}");
                }
            }

            _pcmBuffer.Drain(instance);
            _pipeline.Render(instance, width, height, _renderScale, _transparent);
        }
        finally
        {
            instance.InGlScope = false;
        }

        _eglSurface!.SwapBuffers();
        session.AfterSwap(_eglDisplay!.Handle, _eglSurface.Handle);
    }

    private void EnsureInstance()
    {
        if (_instance is not null || _failed)
        {
            return;
        }

        try
        {
            var instance = ProjectM.Create(GetGlFunction);
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
                _dispatcher.Post(() =>
                {
                    if (!_disposed)
                    {
                        InstanceCreated?.Invoke(this, EventArgs.Empty);
                    }
                });
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
            _dispatcher.Post(() =>
            {
                if (!_disposed)
                {
                    InitializationFailed?.Invoke(this, ex);
                }
            });
        }
    }

    private int NextFrameDelayMs()
    {
        var maxFrameRate = _maxFrameRate > 0 ? _maxFrameRate : _session?.RefreshRate ?? 60;
        var now = Stopwatch.GetTimestamp();
        var interval = (long)(Stopwatch.Frequency / maxFrameRate);
        _nextFrameDue = Math.Max(_nextFrameDue + interval, now);
        var delayMs = (int)((_nextFrameDue - now) * 1000 / Stopwatch.Frequency);
        return Math.Max(delayMs, 1);
    }

    private void TeardownOnRenderThread()
    {
        if (_instance is { } instance)
        {
            instance.InGlScope = true;
            _playlist?.Dispose();
            _playlist = null;
            instance.Dispose();
            _instance = null;
        }

        if (_eglDisplay is { } display)
        {
            if (_eglContext is not null)
            {
                _pipeline.Release();
            }

            try
            {
                display.ReleaseCurrent();
            }
            catch (EglException)
            {
                // Teardown must run to completion even without a current context.
            }

            _eglSurface?.Dispose();
            _eglSurface = null;
            _eglContext?.Dispose();
            _eglContext = null;
            display.Dispose();
            _eglDisplay = null;
        }

        _session?.Dispose();
        _session = null;
    }
}
