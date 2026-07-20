using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;

namespace ProjectMDotNet.Avalonia;

public class ProjectMControl : OpenGlControlBase
{
    private const int GlFramebuffer = 0x8D40;

    /// <summary>Whether the visualizer continuously renders frames.</summary>
    public static readonly StyledProperty<bool> IsRenderingProperty =
        AvaloniaProperty.Register<ProjectMControl, bool>(nameof(IsRendering), defaultValue: true);

    /// <summary>Path of the preset to load (file path, <c>file://</c> URL, or <c>idle://</c>).</summary>
    public static readonly StyledProperty<string?> PresetPathProperty =
        AvaloniaProperty.Register<ProjectMControl, string?>(nameof(PresetPath));

    /// <summary>Seconds each preset plays before automatic switching.</summary>
    public static readonly StyledProperty<double> PresetDurationProperty =
        AvaloniaProperty.Register<ProjectMControl, double>(nameof(PresetDuration), defaultValue: 30.0);

    /// <summary>Beat detection sensitivity. No visual effect as of libprojectM 4.2 (see <see cref="ProjectM.BeatSensitivity"/>); scale the PCM you feed in instead.</summary>
    public static readonly StyledProperty<float> BeatSensitivityProperty =
        AvaloniaProperty.Register<ProjectMControl, float>(nameof(BeatSensitivity), defaultValue: 1.0f);

    /// <summary>Whether beat-driven hard cuts are enabled.</summary>
    public static readonly StyledProperty<bool> HardCutEnabledProperty =
        AvaloniaProperty.Register<ProjectMControl, bool>(nameof(HardCutEnabled), defaultValue: false);

    /// <summary>Whether automatic preset switching is disabled.</summary>
    public static readonly StyledProperty<bool> PresetLockedProperty =
        AvaloniaProperty.Register<ProjectMControl, bool>(nameof(PresetLocked), defaultValue: false);

    /// <summary>
    /// When true, the visualization's black background is composited as transparency.
    /// </summary>
    public static readonly StyledProperty<bool> TransparentBackgroundProperty =
        AvaloniaProperty.Register<ProjectMControl, bool>(nameof(TransparentBackground), defaultValue: false);

    private readonly PcmBuffer _pcmBuffer = new();
    private readonly ConcurrentQueue<Action> _glActions = new();
    private ProjectM? _instance;
    private ProjectMPlaylist? _playlist;
    private TransparencyCompositor? _compositor;
    private bool _compositorFailureLogged;
    private (int Width, int Height) _lastWindowSize;

    /// <summary>The native visualizer instance; non-null after <see cref="InstanceCreated"/> and until GL teardown.</summary>
    public ProjectM? Instance => _instance;

    /// <summary>The playlist created by <see cref="EnablePlaylist"/>, if any.</summary>
    public ProjectMPlaylist? Playlist => _playlist;

    /// <summary>The error that prevented visualizer initialization, if any.</summary>
    public Exception? InitializationError { get; private set; }

    /// <summary>Raised on the UI thread after the native instance has been created.</summary>
    public event EventHandler? InstanceCreated;

    /// <summary>Raised on the UI thread just before the native instance is destroyed.</summary>
    public event EventHandler? InstanceDestroying;

    /// <summary>Raised when the visualizer could not be initialized.</summary>
    public event EventHandler<Exception>? InitializationFailed;

    /// <inheritdoc cref="IsRenderingProperty"/>
    public bool IsRendering
    {
        get => GetValue(IsRenderingProperty);
        set => SetValue(IsRenderingProperty, value);
    }

    /// <inheritdoc cref="PresetPathProperty"/>
    public string? PresetPath
    {
        get => GetValue(PresetPathProperty);
        set => SetValue(PresetPathProperty, value);
    }

    /// <inheritdoc cref="PresetDurationProperty"/>
    public double PresetDuration
    {
        get => GetValue(PresetDurationProperty);
        set => SetValue(PresetDurationProperty, value);
    }

    /// <inheritdoc cref="BeatSensitivityProperty"/>
    public float BeatSensitivity
    {
        get => GetValue(BeatSensitivityProperty);
        set => SetValue(BeatSensitivityProperty, value);
    }

    /// <inheritdoc cref="HardCutEnabledProperty"/>
    public bool HardCutEnabled
    {
        get => GetValue(HardCutEnabledProperty);
        set => SetValue(HardCutEnabledProperty, value);
    }

    /// <inheritdoc cref="PresetLockedProperty"/>
    public bool PresetLocked
    {
        get => GetValue(PresetLockedProperty);
        set => SetValue(PresetLockedProperty, value);
    }

    /// <inheritdoc cref="TransparentBackgroundProperty"/>
    public bool TransparentBackground
    {
        get => GetValue(TransparentBackgroundProperty);
        set => SetValue(TransparentBackgroundProperty, value);
    }

    /// <summary>Queues interleaved float PCM (−1..1) for the visualizer. Callable from any thread.</summary>
    public void AddPcm(ReadOnlySpan<float> interleavedSamples, AudioChannels channels) =>
        _pcmBuffer.Add(interleavedSamples, channels);

    /// <summary>Queues interleaved 16-bit PCM for the visualizer. Callable from any thread.</summary>
    public void AddPcm(ReadOnlySpan<short> interleavedSamples, AudioChannels channels) =>
        _pcmBuffer.Add(interleavedSamples, channels);

    /// <summary>
    /// Queues an action to run just before the next rendered frame, with the
    /// control's OpenGL context current.
    /// </summary>
    public void RunWithGlContext(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _glActions.Enqueue(action);
        RequestNextFrameRendering();
    }

    /// <summary>
    /// Creates (or returns) the native playlist for this control. Only valid
    /// after <see cref="InstanceCreated"/>; call it from that event or later.
    /// </summary>
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

    /// <inheritdoc />
    protected override void OnOpenGlInit(GlInterface gl)
    {
        base.OnOpenGlInit(gl);
        try
        {
            var isGlesPlatform = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
            if (GlVersion.Type == GlProfileType.OpenGLES && !isGlesPlatform)
            {
                var hint = OperatingSystem.IsWindows()
                    ? " On Windows, configure Win32PlatformOptions.RenderingMode with Win32RenderingMode.Wgl."
                    : string.Empty;
                throw new PlatformNotSupportedException(
                    $"OpenGL 3.3 required, {GlVersion.Major}.{GlVersion.Minor}.{hint} provided");
            }

            if (isGlesPlatform && GlVersion is { Type: GlProfileType.OpenGLES, Major: < 3 })
            {
                throw new PlatformNotSupportedException(
                    $"OpenGL ES 3.0 or later required, {GlVersion.Major}.{GlVersion.Minor} provided");
            }

            _instance = ProjectM.Create(name => gl.GetProcAddress(name));
            _instance.GlWorkDispatcher = RunWithGlContext;
            _instance.InGlScope = true;
            try
            {
                _lastWindowSize = default;
                ApplyProperties(_instance);
                _instance.LoadPresetFile(PresetPath ?? "idle://", smoothTransition: false);
                InitializationError = null;
                InstanceCreated?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _instance.InGlScope = false;
            }
        }
        catch (Exception ex)
        {
            InitializationError = ex;
            _instance?.Dispose();
            _instance = null;
            InitializationFailed?.Invoke(this, ex);
        }
    }

    /// <inheritdoc />
    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_instance is { } instance)
        {
            instance.InGlScope = true;
            try
            {
                var size = GetPixelSize();
                if (size != _lastWindowSize)
                {
                    instance.WindowSize = size;
                    _lastWindowSize = size;
                }

                gl.Viewport(0, 0, size.Width, size.Height);

                while (_glActions.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        Trace.TraceError($"ProjectMControl GL action failed: {ex}");
                    }
                }

                _pcmBuffer.Drain(instance);

                TransparencyCompositor? compositor = null;
                var useCompositor = false;
                if (TransparentBackground)
                {
                    try
                    {
                        compositor = _compositor ??= new TransparencyCompositor(
                            GlVersion.Type == GlProfileType.OpenGLES);
                        useCompositor = compositor.EnsureResources(gl, size);
                        if (!useCompositor && !_compositorFailureLogged)
                        {
                            Trace.TraceError("ProjectMControl: transparency framebuffer incomplete; rendering opaque.");
                            _compositorFailureLogged = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_compositorFailureLogged)
                        {
                            Trace.TraceError($"ProjectMControl: transparency compositor failed: {ex}");
                            _compositorFailureLogged = true;
                        }
                    }
                }

                if (useCompositor && compositor is not null)
                {
                    gl.BindFramebuffer(GlFramebuffer, (int)compositor.Framebuffer);
                    gl.Viewport(0, 0, size.Width, size.Height);
                    instance.RenderFrame(compositor.Framebuffer);
                    compositor.Composite(gl, fb, size);
                }
                else
                {
                    instance.RenderFrame((uint)fb);
                }

                gl.BindFramebuffer(GlFramebuffer, fb);
            }
            finally
            {
                instance.InGlScope = false;
            }
        }

        if (IsRendering)
        {
            RequestNextFrameRendering();
        }
    }

    /// <inheritdoc />
    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _glActions.Clear();
        if (_instance is { } instance)
        {
            instance.InGlScope = true;
        }

        InstanceDestroying?.Invoke(this, EventArgs.Empty);
        _compositor?.Release(gl);
        _compositor = null;
        _playlist?.Dispose();
        _playlist = null;
        _instance?.Dispose();
        _instance = null;
        _pcmBuffer.Clear();
        base.OnOpenGlDeinit(gl);
    }

    /// <inheritdoc />
    protected override void OnOpenGlLost()
    {
        _glActions.Clear();
        InstanceDestroying?.Invoke(this, EventArgs.Empty);
        _compositor = null; // context gone; resources die with it
        _playlist?.Abandon();
        _playlist = null;
        _instance?.Abandon();
        _instance = null;
        _pcmBuffer.Clear();
        base.OnOpenGlLost();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsRenderingProperty)
        {
            if (change.GetNewValue<bool>())
            {
                RequestNextFrameRendering();
            }

            return;
        }

        if (_instance is not { } instance)
        {
            return;
        }

        if (change.Property == PresetPathProperty)
        {
            instance.LoadPresetFile(change.GetNewValue<string?>() ?? "idle://", smoothTransition: true);
        }
        else if (change.Property == PresetDurationProperty)
        {
            instance.PresetDuration = change.GetNewValue<double>();
        }
        else if (change.Property == BeatSensitivityProperty)
        {
            instance.BeatSensitivity = change.GetNewValue<float>();
        }
        else if (change.Property == HardCutEnabledProperty)
        {
            instance.HardCutEnabled = change.GetNewValue<bool>();
        }
        else if (change.Property == PresetLockedProperty)
        {
            instance.PresetLocked = change.GetNewValue<bool>();
        }
    }

    private void ApplyProperties(ProjectM instance)
    {
        instance.PresetDuration = PresetDuration;
        instance.BeatSensitivity = BeatSensitivity;
        instance.HardCutEnabled = HardCutEnabled;
        instance.PresetLocked = PresetLocked;
        instance.AspectCorrection = true;
    }

    private (int Width, int Height) GetPixelSize()
    {
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        return (Math.Max(1, (int)(Bounds.Width * scaling)), Math.Max(1, (int)(Bounds.Height * scaling)));
    }
}
