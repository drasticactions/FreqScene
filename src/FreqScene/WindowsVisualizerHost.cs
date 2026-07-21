using System.Diagnostics;
using Avalonia.Threading;
using ProjectMDotNet;

namespace FreqScene;

internal sealed unsafe class WindowsVisualizerHost : IVisualizerHost, IDisposable
{
    private const int WglDrawToWindowArb = 0x2001;
    private const int WglAccelerationArb = 0x2003;
    private const int WglSupportOpenGlArb = 0x2010;
    private const int WglDoubleBufferArb = 0x2011;
    private const int WglPixelTypeArb = 0x2013;
    private const int WglColorBitsArb = 0x2014;
    private const int WglAlphaBitsArb = 0x201B;
    private const int WglDepthBitsArb = 0x2022;
    private const int WglFullAccelerationArb = 0x2027;
    private const int WglTypeRgbaArb = 0x202B;
    private const int WglContextMajorVersionArb = 0x2091;
    private const int WglContextMinorVersionArb = 0x2092;
    private const int WglContextProfileMaskArb = 0x9126;
    private const int WglContextCoreProfileBitArb = 0x0001;

    private const uint PfdDrawToWindow = 0x0000_0004;
    private const uint PfdSupportOpenGl = 0x0000_0020;
    private const uint PfdDoubleBuffer = 0x0000_0001;
    private const uint PfdSupportComposition = 0x0000_8000;

    private const uint MonitorDefaultToNearest = 2;
    private const uint EnumCurrentSettings = uint.MaxValue;

    private readonly IntPtr _hwnd;
    private readonly bool _transparent;
    private readonly PcmBuffer _pcmBuffer = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _glActions = new();
    private readonly GlFramePipeline _pipeline = new();
    private readonly ManualResetEvent _stopEvent = new(false);

    private Thread? _renderThread;
    private IntPtr _dc;
    private IntPtr _glContext;
    private delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr> _createContextAttribs;
    private delegate* unmanaged<IntPtr, int*, float*, uint, int*, uint*, int> _choosePixelFormat;
    private delegate* unmanaged<int, int> _swapInterval;
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
    private double _cachedRefreshRate = 60;
    private long _refreshRateExpiry;

    public WindowsVisualizerHost(IntPtr hwnd, bool transparent)
    {
        _hwnd = hwnd;
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

    public void SetWallpaperBackground(WallpaperBackground? background) =>
        RunWithGlContext(() => _pipeline.SetWallpaperBackground(background));

    /// <summary>Queues an action to run before the next frame on the render thread.</summary>
    public void RunWithGlContext(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _glActions.Enqueue(action);
    }

    /// <summary>Starts the render thread. Call after the window is on screen.</summary>
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

        WindowsInterop.TimeBeginPeriod(1);
        try
        {
            while (!_stopEvent.WaitOne(NextFrameDelayMs()))
            {
                try
                {
                    RenderCore();
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"WindowsVisualizerHost frame failed: {ex}");
                }
            }
        }
        finally
        {
            WindowsInterop.TimeEndPeriod(1);
            TeardownOnRenderThread();
        }
    }

    private void CreateContext()
    {
        LoadWglExtensions();

        _dc = WindowsInterop.GetDC(_hwnd);
        if (_dc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC failed for the visualizer window.");
        }

        var pfd = new WindowsInterop.PixelFormatDescriptor
        {
            Size = (ushort)sizeof(WindowsInterop.PixelFormatDescriptor),
            Version = 1,
            Flags = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer | PfdSupportComposition,
            ColorBits = 32,
            AlphaBits = 8,
            DepthBits = 24,
        };

        var format = 0;
        if (_choosePixelFormat is not null)
        {
            int* attribs = stackalloc int[]
            {
                WglDrawToWindowArb, 1,
                WglSupportOpenGlArb, 1,
                WglDoubleBufferArb, 1,
                WglPixelTypeArb, WglTypeRgbaArb,
                WglColorBitsArb, 32,
                WglAlphaBitsArb, 8,
                WglDepthBitsArb, 24,
                WglAccelerationArb, WglFullAccelerationArb,
                0,
            };
            int chosen;
            uint count;
            if (_choosePixelFormat(_dc, attribs, null, 1, &chosen, &count) != 0 && count > 0)
            {
                format = chosen;
            }
        }

        if (format == 0)
        {
            format = WindowsInterop.ChoosePixelFormat(_dc, ref pfd);
        }

        if (format == 0)
        {
            throw new InvalidOperationException("No usable pixel format is available.");
        }

        WindowsInterop.DescribePixelFormat(_dc, format, (uint)sizeof(WindowsInterop.PixelFormatDescriptor), ref pfd);
        if (!WindowsInterop.SetPixelFormat(_dc, format, ref pfd))
        {
            throw new InvalidOperationException("SetPixelFormat failed.");
        }

        if (_createContextAttribs is not null)
        {
            int* contextAttribs = stackalloc int[]
            {
                WglContextMajorVersionArb, 3,
                WglContextMinorVersionArb, 3,
                WglContextProfileMaskArb, WglContextCoreProfileBitArb,
                0,
            };
            _glContext = _createContextAttribs(_dc, IntPtr.Zero, contextAttribs);
        }

        if (_glContext == IntPtr.Zero)
        {
            throw new InvalidOperationException("An OpenGL 3.3 core context could not be created.");
        }

        if (!WindowsInterop.WglMakeCurrent(_dc, _glContext))
        {
            throw new InvalidOperationException("wglMakeCurrent failed.");
        }

        // Never block on vsync; frame pacing is ours.
        if (_swapInterval is not null)
        {
            _swapInterval(0);
        }

        Gl.Initialize(GetGlFunction);
    }

    private void LoadWglExtensions()
    {
        var className = EnsureBootstrapClass();

        var dummy = WindowsInterop.CreateWindowExW(
            0, className, string.Empty, 0, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, WindowsInterop.GetModuleHandleW(null), IntPtr.Zero);
        if (dummy == IntPtr.Zero)
        {
            throw new InvalidOperationException("The WGL bootstrap window could not be created.");
        }

        var dc = IntPtr.Zero;
        var context = IntPtr.Zero;
        try
        {
            dc = WindowsInterop.GetDC(dummy);
            var pfd = new WindowsInterop.PixelFormatDescriptor
            {
                Size = (ushort)sizeof(WindowsInterop.PixelFormatDescriptor),
                Version = 1,
                Flags = PfdDrawToWindow | PfdSupportOpenGl | PfdDoubleBuffer,
                ColorBits = 32,
                DepthBits = 24,
            };
            var format = WindowsInterop.ChoosePixelFormat(dc, ref pfd);
            if (format == 0 || !WindowsInterop.SetPixelFormat(dc, format, ref pfd))
            {
                throw new InvalidOperationException("The WGL bootstrap pixel format could not be set.");
            }

            context = WindowsInterop.WglCreateContext(dc);
            if (context == IntPtr.Zero || !WindowsInterop.WglMakeCurrent(dc, context))
            {
                throw new InvalidOperationException("The WGL bootstrap context could not be created.");
            }

            _createContextAttribs = (delegate* unmanaged<IntPtr, IntPtr, int*, IntPtr>)
                WindowsInterop.WglGetProcAddress("wglCreateContextAttribsARB");
            _choosePixelFormat = (delegate* unmanaged<IntPtr, int*, float*, uint, int*, uint*, int>)
                WindowsInterop.WglGetProcAddress("wglChoosePixelFormatARB");
            _swapInterval = (delegate* unmanaged<int, int>)
                WindowsInterop.WglGetProcAddress("wglSwapIntervalEXT");
        }
        finally
        {
            WindowsInterop.WglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            if (context != IntPtr.Zero)
            {
                WindowsInterop.WglDeleteContext(context);
            }

            if (dc != IntPtr.Zero)
            {
                WindowsInterop.ReleaseDC(dummy, dc);
            }

            WindowsInterop.DestroyWindow(dummy);
        }
    }

    private static string? s_bootstrapClass;

    private static string EnsureBootstrapClass()
    {
        if (s_bootstrapClass is not null)
        {
            return s_bootstrapClass;
        }

        const string name = "FreqSceneGlBootstrap";
        var wndClass = new WindowsInterop.WndClassExW
        {
            Size = (uint)sizeof(WindowsInterop.WndClassExW),
            WndProc = WindowsInterop.GetProcAddress(
                WindowsInterop.GetModuleHandleW("user32.dll"), "DefWindowProcW"),
            Instance = WindowsInterop.GetModuleHandleW(null),
        };
        fixed (char* className = name)
        {
            wndClass.ClassName = (IntPtr)className;
            if (WindowsInterop.RegisterClassExW(ref wndClass) == 0)
            {
                throw new InvalidOperationException("The WGL bootstrap window class could not be registered.");
            }
        }

        s_bootstrapClass = name;
        return name;
    }

    private static IntPtr GetGlFunction(string name)
    {
        var pointer = WindowsInterop.WglGetProcAddress(name);
        var value = pointer.ToInt64();
        if (value is >= -1 and <= 3)
        {
            pointer = WindowsInterop.GetProcAddress(WindowsInterop.GetModuleHandleW("opengl32.dll"), name);
        }

        return pointer;
    }

    private void RenderCore()
    {
        if (!WindowsInterop.GetClientRect(_hwnd, out var rect))
        {
            return;
        }

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
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
                    Trace.TraceError($"WindowsVisualizerHost GL action failed: {ex}");
                }
            }

            _pcmBuffer.Drain(instance);
            _pipeline.Render(instance, width, height, _renderScale, _transparent);
        }
        finally
        {
            instance.InGlScope = false;
        }

        WindowsInterop.SwapBuffers(_dc);
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
        var maxFrameRate = _maxFrameRate > 0 ? _maxFrameRate : DisplayFrameRate();
        var now = Stopwatch.GetTimestamp();
        var interval = (long)(Stopwatch.Frequency / maxFrameRate);
        _nextFrameDue = Math.Max(_nextFrameDue + interval, now);
        var delayMs = (int)((_nextFrameDue - now) * 1000 / Stopwatch.Frequency);
        return Math.Max(delayMs, 1);
    }

    private double DisplayFrameRate()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _refreshRateExpiry)
        {
            return _cachedRefreshRate;
        }

        _refreshRateExpiry = now + Stopwatch.Frequency * 2;

        var monitor = WindowsInterop.MonitorFromWindow(_hwnd, MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero)
        {
            var info = new WindowsInterop.MonitorInfoExW
            {
                Size = (uint)sizeof(WindowsInterop.MonitorInfoExW),
            };
            if (WindowsInterop.GetMonitorInfoW(monitor, ref info))
            {
                var device = new string(info.Device, 0, DeviceNameLength(info.Device));
                var mode = new WindowsInterop.DevModeW
                {
                    Size = (ushort)sizeof(WindowsInterop.DevModeW),
                };
                if (WindowsInterop.EnumDisplaySettingsW(device, EnumCurrentSettings, ref mode) &&
                    mode.DisplayFrequency > 1)
                {
                    _cachedRefreshRate = mode.DisplayFrequency;
                    return _cachedRefreshRate;
                }
            }
        }

        _cachedRefreshRate = 60;
        return _cachedRefreshRate;
    }

    private static int DeviceNameLength(char* device)
    {
        var length = 0;
        while (length < 32 && device[length] != '\0')
        {
            length++;
        }

        return length;
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

        if (_glContext != IntPtr.Zero)
        {
            _pipeline.Release();
            WindowsInterop.WglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
            WindowsInterop.WglDeleteContext(_glContext);
            _glContext = IntPtr.Zero;
        }

        if (_dc != IntPtr.Zero)
        {
            WindowsInterop.ReleaseDC(_hwnd, _dc);
            _dc = IntPtr.Zero;
        }
    }
}
