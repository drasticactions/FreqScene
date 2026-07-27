using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ProjectMDotNet;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Graphics.OpenGL;
using Windows.Win32.UI.WindowsAndMessaging;

namespace FreqScene;

[SupportedOSPlatform("windows10.0.14393")]
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

    private readonly HWND _hwnd;
    private readonly bool _transparent;
    private readonly PcmBuffer _pcmBuffer = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _glActions = new();
    private readonly GlFramePipeline _pipeline = new();
    private readonly ManualResetEvent _stopEvent = new(false);

    private Thread? _renderThread;
    private HDC _dc;
    private HGLRC _glContext;
    private delegate* unmanaged<HDC, HGLRC, int*, HGLRC> _createContextAttribs;
    private delegate* unmanaged<HDC, int*, float*, uint, int*, uint*, int> _choosePixelFormat;
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

    private readonly ILogger _logger;

    public WindowsVisualizerHost(HWND hwnd, bool transparent, ILoggerFactory? loggerFactory = null)
    {
        _hwnd = hwnd;
        _transparent = transparent;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WindowsVisualizerHost>();
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

        PInvoke.timeBeginPeriod(1);
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
                    _logger.LogError(ex, "frame failed");
                }
            }
        }
        finally
        {
            PInvoke.timeEndPeriod(1);
            TeardownOnRenderThread();
        }
    }

    private void CreateContext()
    {
        LoadWglExtensions();

        _dc = PInvoke.GetDC(_hwnd);
        if (_dc.IsNull)
        {
            throw new InvalidOperationException("GetDC failed for the visualizer window.");
        }

        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)sizeof(PIXELFORMATDESCRIPTOR),
            nVersion = 1,
            dwFlags = PFD_FLAGS.PFD_DRAW_TO_WINDOW | PFD_FLAGS.PFD_SUPPORT_OPENGL |
                PFD_FLAGS.PFD_DOUBLEBUFFER | PFD_FLAGS.PFD_SUPPORT_COMPOSITION,
            cColorBits = 32,
            cAlphaBits = 8,
            cDepthBits = 24,
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
            format = PInvoke.ChoosePixelFormat(_dc, in pfd);
        }

        if (format == 0)
        {
            throw new InvalidOperationException("No usable pixel format is available.");
        }

        PInvoke.DescribePixelFormat(_dc, format, (uint)sizeof(PIXELFORMATDESCRIPTOR), &pfd);
        if (!PInvoke.SetPixelFormat(_dc, format, in pfd))
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
            _glContext = _createContextAttribs(_dc, HGLRC.Null, contextAttribs);
        }

        if (_glContext.IsNull)
        {
            throw new InvalidOperationException("An OpenGL 3.3 core context could not be created.");
        }

        if (!PInvoke.wglMakeCurrent(_dc, _glContext))
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

        var dummy = PInvoke.CreateWindowEx(
            0, className, string.Empty, 0, 0, 0, 1, 1,
            HWND.Null, null, PInvoke.GetModuleHandle((string?)null), null);
        if (dummy.IsNull)
        {
            throw new InvalidOperationException("The WGL bootstrap window could not be created.");
        }

        var dc = HDC.Null;
        var context = HGLRC.Null;
        try
        {
            dc = PInvoke.GetDC(dummy);
            var pfd = new PIXELFORMATDESCRIPTOR
            {
                nSize = (ushort)sizeof(PIXELFORMATDESCRIPTOR),
                nVersion = 1,
                dwFlags = PFD_FLAGS.PFD_DRAW_TO_WINDOW | PFD_FLAGS.PFD_SUPPORT_OPENGL | PFD_FLAGS.PFD_DOUBLEBUFFER,
                cColorBits = 32,
                cDepthBits = 24,
            };
            var format = PInvoke.ChoosePixelFormat(dc, in pfd);
            if (format == 0 || !PInvoke.SetPixelFormat(dc, format, in pfd))
            {
                throw new InvalidOperationException("The WGL bootstrap pixel format could not be set.");
            }

            context = PInvoke.wglCreateContext(dc);
            if (context.IsNull || !PInvoke.wglMakeCurrent(dc, context))
            {
                throw new InvalidOperationException("The WGL bootstrap context could not be created.");
            }

            _createContextAttribs = (delegate* unmanaged<HDC, HGLRC, int*, HGLRC>)
                (IntPtr)PInvoke.wglGetProcAddress("wglCreateContextAttribsARB");
            _choosePixelFormat = (delegate* unmanaged<HDC, int*, float*, uint, int*, uint*, int>)
                (IntPtr)PInvoke.wglGetProcAddress("wglChoosePixelFormatARB");
            _swapInterval = (delegate* unmanaged<int, int>)
                (IntPtr)PInvoke.wglGetProcAddress("wglSwapIntervalEXT");
        }
        finally
        {
            PInvoke.wglMakeCurrent(HDC.Null, HGLRC.Null);
            if (!context.IsNull)
            {
                PInvoke.wglDeleteContext(context);
            }

            if (!dc.IsNull)
            {
                PInvoke.ReleaseDC(dummy, dc);
            }

            PInvoke.DestroyWindow(dummy);
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
        var wndClass = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = &BootstrapWndProc,
            hInstance = PInvoke.GetModuleHandle(default(PCWSTR)),
        };
        fixed (char* className = name)
        {
            wndClass.lpszClassName = className;
            if (PInvoke.RegisterClassEx(in wndClass) == 0)
            {
                throw new InvalidOperationException("The WGL bootstrap window class could not be registered.");
            }
        }

        s_bootstrapClass = name;
        return name;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static LRESULT BootstrapWndProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam) =>
        PInvoke.DefWindowProc(hwnd, message, wParam, lParam);

    private static IntPtr GetGlFunction(string name)
    {
        IntPtr pointer = PInvoke.wglGetProcAddress(name);
        var value = pointer.ToInt64();
        if (value is >= -1 and <= 3)
        {
            pointer = PInvoke.GetProcAddress(PInvoke.GetModuleHandle("opengl32.dll"), name);
        }

        return pointer;
    }

    private void RenderCore()
    {
        if (!PInvoke.GetClientRect(_hwnd, out var rect))
        {
            return;
        }

        var width = rect.Width;
        var height = rect.Height;
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
                    _logger.LogError(ex, "GL action failed");
                }
            }

            _pcmBuffer.Drain(instance);
            _pipeline.Render(instance, width, height, _renderScale, _transparent);
        }
        finally
        {
            instance.InGlScope = false;
        }

        PInvoke.SwapBuffers(_dc);
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

        var monitor = PInvoke.MonitorFromWindow(_hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (!monitor.IsNull)
        {
            var info = default(MONITORINFOEXW);
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info))
            {
                var mode = default(DEVMODEW);
                mode.dmSize = (ushort)sizeof(DEVMODEW);
                if (PInvoke.EnumDisplaySettings(
                        info.szDevice.ToString(), ENUM_DISPLAY_SETTINGS_MODE.ENUM_CURRENT_SETTINGS, ref mode) &&
                    mode.dmDisplayFrequency > 1)
                {
                    _cachedRefreshRate = mode.dmDisplayFrequency;
                    return _cachedRefreshRate;
                }
            }
        }

        _cachedRefreshRate = 60;
        return _cachedRefreshRate;
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

        if (!_glContext.IsNull)
        {
            _pipeline.Release();
            PInvoke.wglMakeCurrent(HDC.Null, HGLRC.Null);
            PInvoke.wglDeleteContext(_glContext);
            _glContext = HGLRC.Null;
        }

        if (!_dc.IsNull)
        {
            PInvoke.ReleaseDC(_hwnd, _dc);
            _dc = HDC.Null;
        }
    }
}
