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

    private const string VertexSource = """
        #version 330 core
        out vec2 uv;
        void main()
        {
            vec2 pos = vec2(gl_VertexID == 1 || gl_VertexID == 3 ? 1.0 : -1.0,
                            gl_VertexID >= 2 ? 1.0 : -1.0);
            uv = pos * 0.5 + 0.5;
            gl_Position = vec4(pos, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec2 uv;
        out vec4 fragColor;
        uniform sampler2D source;
        void main()
        {
            vec3 color = texture(source, uv).rgb;
            float alpha = max(color.r, max(color.g, color.b));
            fragColor = vec4(color, alpha);
        }
        """;

    private readonly IntPtr _window;
    private readonly IntPtr _view;
    private readonly bool _transparent;
    private readonly PcmBuffer _pcmBuffer = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<Action> _glActions = new();

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
    private (int Width, int Height) _lastWindowSize;
    private MacInterop.CgRect _lastFrame;
    private double _lastBackingScale;

    // Offscreen target used for reduced-resolution rendering and the
    // transparency composite; unused (zero) when rendering directly.
    private uint _fbo;
    private uint _fboTexture;
    private uint _fboDepth;
    private (int Width, int Height) _fboSize;
    private uint _program;
    private uint _vao;

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
            ReleaseRenderResources();
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

            var scale = _renderScale;
            var useOffscreen = _transparent || scale < 0.999;
            if (useOffscreen)
            {
                var scaled = (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
                EnsureOffscreen(scaled);
                SetWindowSize(instance, scaled);
                MacGl.BindFramebuffer(MacGl.Framebuffer, _fbo);
                MacGl.Viewport(0, 0, scaled.Item1, scaled.Item2);
                instance.RenderFrame(_fbo);

                if (_transparent)
                {
                    Composite(width, height);
                }
                else
                {
                    MacGl.BindFramebuffer(MacGl.ReadFramebuffer, _fbo);
                    MacGl.BindFramebuffer(MacGl.DrawFramebuffer, 0);
                    MacGl.BlitFramebuffer(
                        0, 0, scaled.Item1, scaled.Item2, 0, 0, width, height, MacGl.ColorBufferBit, MacGl.Linear);
                }

                MacGl.BindFramebuffer(MacGl.Framebuffer, 0);
            }
            else
            {
                SetWindowSize(instance, (width, height));
                MacGl.BindFramebuffer(MacGl.Framebuffer, 0);
                MacGl.Viewport(0, 0, width, height);
                instance.RenderFrame(0);
            }
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
                _lastWindowSize = default;
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

    private void SetWindowSize(ProjectM instance, (int Width, int Height) size)
    {
        if (size != _lastWindowSize)
        {
            instance.WindowSize = size;
            _lastWindowSize = size;
        }
    }

    private void EnsureOffscreen((int Width, int Height) size)
    {
        if (_fbo == 0)
        {
            MacGl.GenFramebuffers(1, out _fbo);
        }

        if (_fboSize == size)
        {
            return;
        }

        if (_fboTexture != 0)
        {
            MacGl.DeleteTextures(1, in _fboTexture);
        }

        if (_fboDepth != 0)
        {
            MacGl.DeleteRenderbuffers(1, in _fboDepth);
        }

        MacGl.GenTextures(1, out _fboTexture);
        MacGl.BindTexture(MacGl.Texture2D, _fboTexture);
        MacGl.TexImage2D(MacGl.Texture2D, 0, MacGl.Rgba8, size.Width, size.Height, 0, MacGl.Rgba, MacGl.UnsignedByte, IntPtr.Zero);
        MacGl.TexParameteri(MacGl.Texture2D, MacGl.TextureMinFilter, MacGl.Linear);
        MacGl.TexParameteri(MacGl.Texture2D, MacGl.TextureMagFilter, MacGl.Linear);
        MacGl.TexParameteri(MacGl.Texture2D, MacGl.TextureWrapS, MacGl.ClampToEdge);
        MacGl.TexParameteri(MacGl.Texture2D, MacGl.TextureWrapT, MacGl.ClampToEdge);
        MacGl.BindTexture(MacGl.Texture2D, 0);

        MacGl.GenRenderbuffers(1, out _fboDepth);
        MacGl.BindRenderbuffer(MacGl.Renderbuffer, _fboDepth);
        MacGl.RenderbufferStorage(MacGl.Renderbuffer, MacGl.DepthComponent24, size.Width, size.Height);
        MacGl.BindRenderbuffer(MacGl.Renderbuffer, 0);

        MacGl.BindFramebuffer(MacGl.Framebuffer, _fbo);
        MacGl.FramebufferTexture2D(MacGl.Framebuffer, MacGl.ColorAttachment0, MacGl.Texture2D, _fboTexture, 0);
        MacGl.FramebufferRenderbuffer(MacGl.Framebuffer, MacGl.DepthAttachment, MacGl.Renderbuffer, _fboDepth);
        var status = MacGl.CheckFramebufferStatus(MacGl.Framebuffer);
        MacGl.BindFramebuffer(MacGl.Framebuffer, 0);
        if (status != MacGl.FramebufferComplete)
        {
            throw new InvalidOperationException($"Offscreen framebuffer incomplete: 0x{status:X}");
        }

        _fboSize = size;
    }

    private void Composite(int width, int height)
    {
        if (_program == 0)
        {
            var vertex = MacGl.CompileShaderChecked(MacGl.VertexShader, VertexSource);
            var fragment = MacGl.CompileShaderChecked(MacGl.FragmentShader, FragmentSource);
            _program = MacGl.LinkProgramChecked(vertex, fragment);
            MacGl.DeleteShader(vertex);
            MacGl.DeleteShader(fragment);
            MacGl.GenVertexArrays(1, out _vao);
        }

        MacGl.BindFramebuffer(MacGl.Framebuffer, 0);
        MacGl.Viewport(0, 0, width, height);
        MacGl.Disable(MacGl.DepthTest);
        MacGl.Disable(MacGl.Blend);
        MacGl.Disable(MacGl.ScissorTest);
        MacGl.Disable(MacGl.CullFace);

        MacGl.UseProgram(_program);
        MacGl.ActiveTexture(MacGl.Texture0);
        MacGl.BindTexture(MacGl.Texture2D, _fboTexture);
        MacGl.Uniform1i(MacGl.GetUniformLocation(_program, "source"), 0);
        MacGl.BindVertexArray(_vao);
        MacGl.DrawArrays(MacGl.TriangleStrip, 0, 4);
        MacGl.BindVertexArray(0);
        MacGl.BindTexture(MacGl.Texture2D, 0);
        MacGl.UseProgram(0);
    }

    private void ReleaseRenderResources()
    {
        if (_fboTexture != 0)
        {
            MacGl.DeleteTextures(1, in _fboTexture);
            _fboTexture = 0;
        }

        if (_fboDepth != 0)
        {
            MacGl.DeleteRenderbuffers(1, in _fboDepth);
            _fboDepth = 0;
        }

        if (_fbo != 0)
        {
            MacGl.DeleteFramebuffers(1, in _fbo);
            _fbo = 0;
        }

        if (_vao != 0)
        {
            MacGl.DeleteVertexArrays(1, in _vao);
            _vao = 0;
        }

        if (_program != 0)
        {
            MacGl.DeleteProgram(_program);
            _program = 0;
        }

        _fboSize = default;
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
