using System.Runtime.InteropServices;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using OpenGLES;
using ProjectMDotNet;
using UIKit;

namespace FreqScene.iOS;

public sealed class ProjectMGLView : UIView
{
    private const uint GL_FRAMEBUFFER = 0x8D40;
    private const uint GL_RENDERBUFFER = 0x8D41;
    private const uint GL_COLOR_ATTACHMENT0 = 0x8CE0;
    private const uint GL_FRAMEBUFFER_COMPLETE = 0x8CD5;
    private const uint GL_RENDERBUFFER_WIDTH = 0x8D42;
    private const uint GL_RENDERBUFFER_HEIGHT = 0x8D43;

    private readonly PcmBuffer _pcm = new();
    private EAGLContext? _context;
    private ProjectM? _projectM;
    private CADisplayLink? _displayLink;
    private Func<string, IntPtr>? _glLoadProc; // kept rooted for the instance lifetime
    private uint _framebuffer;
    private uint _colorRenderbuffer;
    private int _width;
    private int _height;
    private bool _failed;

    [Export("layerClass")]
    public static Class LayerClass() => new(typeof(CAEAGLLayer));

    public ProjectMGLView(CGRect frame)
        : base(frame)
    {
        var eaglLayer = (CAEAGLLayer)Layer;
        eaglLayer.Opaque = true;
        ContentScaleFactor = UIScreen.MainScreen.Scale;
    }

    public void AddPcm(ReadOnlySpan<float> interleavedSamples, AudioChannels channels) =>
        _pcm.Add(interleavedSamples, channels);

    public override void MovedToWindow()
    {
        base.MovedToWindow();
        if (Window is not null && _context is null && !_failed)
        {
            Initialize();
        }
    }

    public override void LayoutSubviews()
    {
        base.LayoutSubviews();
        if (_context is not null)
        {
            ResizeRenderbuffer();
        }
    }

    private void Initialize()
    {
        try
        {
            _context = new EAGLContext(EAGLRenderingAPI.OpenGLES3)
                ?? throw new InvalidOperationException("Failed to create an OpenGL ES 3.0 context.");
            EAGLContext.SetCurrentContext(_context);

            CreateFramebuffer();

            _glLoadProc = static name => Dlsym(RtldDefault, name);
            _projectM = ProjectM.Create(_glLoadProc);
            _projectM.AspectCorrection = true;
            _projectM.PresetDuration = 30.0;
            _projectM.WindowSize = (_width, _height);
            _projectM.LoadPresetFile("idle://", smoothTransition: false);

            _displayLink = CADisplayLink.Create(RenderFrame);
            _displayLink.AddToRunLoop(NSRunLoop.Main, NSRunLoopMode.Common);
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.Error.WriteLine($"ProjectMGLView init failed: {ex}");
            Teardown();
        }
    }

    private void CreateFramebuffer()
    {
        glGenFramebuffers(1, out _framebuffer);
        glBindFramebuffer(GL_FRAMEBUFFER, _framebuffer);

        glGenRenderbuffers(1, out _colorRenderbuffer);
        glBindRenderbuffer(GL_RENDERBUFFER, _colorRenderbuffer);
        _context!.RenderBufferStorage((nuint)GL_RENDERBUFFER, (CAEAGLLayer)Layer);
        glFramebufferRenderbuffer(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_RENDERBUFFER, _colorRenderbuffer);

        glGetRenderbufferParameteriv(GL_RENDERBUFFER, GL_RENDERBUFFER_WIDTH, out _width);
        glGetRenderbufferParameteriv(GL_RENDERBUFFER, GL_RENDERBUFFER_HEIGHT, out _height);

        var status = glCheckFramebufferStatus(GL_FRAMEBUFFER);
        if (status != GL_FRAMEBUFFER_COMPLETE)
        {
            throw new InvalidOperationException($"Framebuffer incomplete: 0x{status:X4}.");
        }
    }

    private void ResizeRenderbuffer()
    {
        EAGLContext.SetCurrentContext(_context);
        glBindRenderbuffer(GL_RENDERBUFFER, _colorRenderbuffer);
        _context!.RenderBufferStorage((nuint)GL_RENDERBUFFER, (CAEAGLLayer)Layer);
        glGetRenderbufferParameteriv(GL_RENDERBUFFER, GL_RENDERBUFFER_WIDTH, out var width);
        glGetRenderbufferParameteriv(GL_RENDERBUFFER, GL_RENDERBUFFER_HEIGHT, out var height);
        if ((width, height) != (_width, _height))
        {
            (_width, _height) = (width, height);
            if (_projectM is { } instance)
            {
                instance.WindowSize = (_width, _height);
            }
        }
    }

    private void RenderFrame()
    {
        if (_projectM is not { } instance || _context is null)
        {
            return;
        }

        EAGLContext.SetCurrentContext(_context);
        glBindFramebuffer(GL_FRAMEBUFFER, _framebuffer);
        glViewport(0, 0, _width, _height);

        _pcm.Drain(instance);
        instance.RenderFrame(_framebuffer);

        glBindRenderbuffer(GL_RENDERBUFFER, _colorRenderbuffer);
        _context.PresentRenderBuffer((nuint)GL_RENDERBUFFER);
    }

    private void Teardown()
    {
        _displayLink?.Invalidate();
        _displayLink = null;

        if (_context is not null)
        {
            EAGLContext.SetCurrentContext(_context);
            _projectM?.Dispose();
            _projectM = null;
            if (_framebuffer != 0)
            {
                glDeleteFramebuffers(1, ref _framebuffer);
                _framebuffer = 0;
            }

            if (_colorRenderbuffer != 0)
            {
                glDeleteRenderbuffers(1, ref _colorRenderbuffer);
                _colorRenderbuffer = 0;
            }

            EAGLContext.SetCurrentContext(null);
            _context.Dispose();
            _context = null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Teardown();
        }

        base.Dispose(disposing);
    }

    private const string LibSystem = "/usr/lib/libSystem.dylib";
    private const string OpenGLES = "/System/Library/Frameworks/OpenGLES.framework/OpenGLES";

    private static readonly IntPtr RtldDefault = -2;

    [DllImport(LibSystem, EntryPoint = "dlsym")]
    private static extern IntPtr Dlsym(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string symbol);

    [DllImport(OpenGLES)]
    private static extern void glGenFramebuffers(int n, out uint framebuffers);

    [DllImport(OpenGLES)]
    private static extern void glBindFramebuffer(uint target, uint framebuffer);

    [DllImport(OpenGLES)]
    private static extern void glDeleteFramebuffers(int n, ref uint framebuffers);

    [DllImport(OpenGLES)]
    private static extern void glGenRenderbuffers(int n, out uint renderbuffers);

    [DllImport(OpenGLES)]
    private static extern void glBindRenderbuffer(uint target, uint renderbuffer);

    [DllImport(OpenGLES)]
    private static extern void glDeleteRenderbuffers(int n, ref uint renderbuffers);

    [DllImport(OpenGLES)]
    private static extern void glFramebufferRenderbuffer(uint target, uint attachment, uint renderbuffertarget, uint renderbuffer);

    [DllImport(OpenGLES)]
    private static extern void glGetRenderbufferParameteriv(uint target, uint pname, out int parameters);

    [DllImport(OpenGLES)]
    private static extern uint glCheckFramebufferStatus(uint target);

    [DllImport(OpenGLES)]
    private static extern void glViewport(int x, int y, int width, int height);
}
