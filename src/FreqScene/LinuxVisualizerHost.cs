using System.Diagnostics;
using Avalonia.Threading;
using ProjectMDotNet;

namespace FreqScene;

internal sealed unsafe class LinuxVisualizerHost : IVisualizerHost, IDisposable
{
    private readonly DisplayMode _mode;
    private readonly string? _displayKey;
    private readonly bool _transparent;
    private readonly PcmBuffer _pcmBuffer = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _glActions = new();
    private readonly GlFramePipeline _pipeline = new();
    private readonly ManualResetEvent _stopEvent = new(false);

    private Thread? _renderThread;
    private LinuxWaylandSession? _session;
    private IntPtr _eglDisplay;
    private IntPtr _eglContext;
    private IntPtr _eglSurface;
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

    public LinuxVisualizerHost(DisplayMode mode, bool wallpaperTransparency, string? displayKey)
    {
        _mode = mode;
        _displayKey = displayKey;
        _transparent = mode == DisplayMode.Overlay ||
            (mode == DisplayMode.Wallpaper && wallpaperTransparency);
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
        _renderThread?.Join();
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
            _session = new LinuxWaylandSession(_mode, _displayKey);
            CreateContext();
        }
        catch (Exception ex)
        {
            _failed = true;
            TeardownOnRenderThread();
            Dispatcher.UIThread.Post(() =>
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
                    if (_session.ClosedByCompositor)
                    {
                        Trace.TraceWarning("[native] the compositor closed the layer surface; rendering stops.");
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
        _eglDisplay = LinuxInterop.eglGetPlatformDisplay(
            LinuxInterop.EglPlatformWaylandKhr, session.DisplayHandle, IntPtr.Zero);
        if (_eglDisplay == IntPtr.Zero)
        {
            _eglDisplay = LinuxInterop.eglGetDisplay(session.DisplayHandle);
        }

        if (_eglDisplay == IntPtr.Zero)
        {
            throw new InvalidOperationException("No EGL display is available for the Wayland connection.");
        }

        if (!LinuxInterop.eglInitialize(_eglDisplay, out _, out _))
        {
            throw new InvalidOperationException($"eglInitialize failed (0x{LinuxInterop.eglGetError():X}).");
        }

        if (!LinuxInterop.eglBindAPI(LinuxInterop.EglOpenGlApi))
        {
            throw new InvalidOperationException("The EGL implementation does not support desktop OpenGL.");
        }

        int* configAttribs = stackalloc int[]
        {
            LinuxInterop.EglSurfaceType, LinuxInterop.EglWindowBit,
            LinuxInterop.EglRenderableType, LinuxInterop.EglOpenGlBit,
            LinuxInterop.EglRedSize, 8,
            LinuxInterop.EglGreenSize, 8,
            LinuxInterop.EglBlueSize, 8,
            LinuxInterop.EglAlphaSize, 8,
            LinuxInterop.EglDepthSize, 24,
            LinuxInterop.EglNone,
        };
        if (!LinuxInterop.eglChooseConfig(_eglDisplay, configAttribs, out var config, 1, out var configCount) ||
            configCount < 1)
        {
            throw new InvalidOperationException("No usable EGL config is available.");
        }

        int* contextAttribs = stackalloc int[]
        {
            LinuxInterop.EglContextMajorVersion, 3,
            LinuxInterop.EglContextMinorVersion, 3,
            LinuxInterop.EglContextOpenGlProfileMask, LinuxInterop.EglContextOpenGlCoreProfileBit,
            LinuxInterop.EglNone,
        };
        _eglContext = LinuxInterop.eglCreateContext(_eglDisplay, config, IntPtr.Zero, contextAttribs);
        if (_eglContext == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"An OpenGL 3.3 core context could not be created (0x{LinuxInterop.eglGetError():X}).");
        }

        _eglSurface = LinuxInterop.eglCreateWindowSurface(_eglDisplay, config, session.EglWindowHandle, null);
        if (_eglSurface == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"eglCreateWindowSurface failed (0x{LinuxInterop.eglGetError():X}).");
        }

        if (!LinuxInterop.eglMakeCurrent(_eglDisplay, _eglSurface, _eglSurface, _eglContext))
        {
            throw new InvalidOperationException($"eglMakeCurrent failed (0x{LinuxInterop.eglGetError():X}).");
        }

        // Never block on vsync; frame pacing is ours.
        LinuxInterop.eglSwapInterval(_eglDisplay, 0);
        Gl.Initialize(GetGlFunction);
    }

    private static IntPtr GetGlFunction(string name)
    {
        var pointer = LinuxInterop.eglGetProcAddress(name);
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

        LinuxInterop.eglSwapBuffers(_eglDisplay, _eglSurface);
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
                Dispatcher.UIThread.Post(() =>
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
            Dispatcher.UIThread.Post(() =>
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

        if (_eglDisplay != IntPtr.Zero)
        {
            if (_eglContext != IntPtr.Zero)
            {
                _pipeline.Release();
            }

            LinuxInterop.eglMakeCurrent(_eglDisplay, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (_eglSurface != IntPtr.Zero)
            {
                LinuxInterop.eglDestroySurface(_eglDisplay, _eglSurface);
                _eglSurface = IntPtr.Zero;
            }

            if (_eglContext != IntPtr.Zero)
            {
                LinuxInterop.eglDestroyContext(_eglDisplay, _eglContext);
                _eglContext = IntPtr.Zero;
            }

            LinuxInterop.eglTerminate(_eglDisplay);
            _eglDisplay = IntPtr.Zero;
        }

        _session?.Dispose();
        _session = null;
    }
}
