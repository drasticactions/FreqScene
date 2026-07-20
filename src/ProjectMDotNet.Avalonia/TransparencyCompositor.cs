using Avalonia.OpenGL;

namespace ProjectMDotNet.Avalonia;

internal sealed class TransparencyCompositor
{
    private readonly bool _isGles;

    public TransparencyCompositor(bool isGles) => _isGles = isGles;

    private const int GlTexture2D = 0x0DE1;
    private const int GlRgba = 0x1908;
    private const int GlRgba8 = 0x8058;
    private const int GlUnsignedByte = 0x1401;
    private const int GlTextureMinFilter = 0x2801;
    private const int GlTextureMagFilter = 0x2800;
    private const int GlLinear = 0x2601;
    private const int GlTextureWrapS = 0x2802;
    private const int GlTextureWrapT = 0x2803;
    private const int GlClampToEdge = 0x812F;
    private const int GlFramebuffer = 0x8D40;
    private const int GlColorAttachment0 = 0x8CE0;
    private const int GlDepthAttachment = 0x8D00;
    private const int GlRenderbuffer = 0x8D41;
    private const int GlDepthComponent24 = 0x81A6;
    private const int GlFramebufferComplete = 0x8CD5;
    private const int GlVertexShader = 0x8B31;
    private const int GlFragmentShader = 0x8B30;
    private const int GlTriangleStrip = 0x0005;
    private const int GlTexture0 = 0x84C0;
    private const int GlDepthTest = 0x0B71;
    private const int GlBlend = 0x0BE2;
    private const int GlScissorTest = 0x0C11;
    private const int GlCullFace = 0x0B44;

    private string VersionHeader => _isGles
        ? "#version 300 es\nprecision mediump float;\n"
        : "#version 330 core\n";

    private const string VertexSource = """
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

    private int _fbo;
    private int _texture;
    private int _depthRenderbuffer;
    private int _program;
    private int _vao;
    private (int Width, int Height) _size;
    private bool _ready;

    public uint Framebuffer => (uint)_fbo;

    public bool EnsureResources(GlInterface gl, (int Width, int Height) size)
    {
        if (_program == 0)
        {
            var vertex = gl.CreateShader(GlVertexShader);
            if (gl.CompileShaderAndGetError(vertex, VersionHeader + VertexSource) is { } vsError)
            {
                throw new ProjectMException($"Transparency vertex shader failed to compile: {vsError}");
            }

            var fragment = gl.CreateShader(GlFragmentShader);
            if (gl.CompileShaderAndGetError(fragment, VersionHeader + FragmentSource) is { } fsError)
            {
                throw new ProjectMException($"Transparency fragment shader failed to compile: {fsError}");
            }

            _program = gl.CreateProgram();
            gl.AttachShader(_program, vertex);
            gl.AttachShader(_program, fragment);
            if (gl.LinkProgramAndGetError(_program) is { } linkError)
            {
                throw new ProjectMException($"Transparency shader failed to link: {linkError}");
            }

            gl.DeleteShader(vertex);
            gl.DeleteShader(fragment);
            _vao = gl.GenVertexArray();
            _fbo = gl.GenFramebuffer();
        }

        if (_size != size || !_ready)
        {
            if (_texture != 0)
            {
                gl.DeleteTexture(_texture);
            }

            if (_depthRenderbuffer != 0)
            {
                gl.DeleteRenderbuffer(_depthRenderbuffer);
            }

            _texture = gl.GenTexture();
            gl.BindTexture(GlTexture2D, _texture);
            gl.TexImage2D(GlTexture2D, 0, GlRgba8, size.Width, size.Height, 0, GlRgba, GlUnsignedByte, IntPtr.Zero);
            gl.TexParameteri(GlTexture2D, GlTextureMinFilter, GlLinear);
            gl.TexParameteri(GlTexture2D, GlTextureMagFilter, GlLinear);
            gl.TexParameteri(GlTexture2D, GlTextureWrapS, GlClampToEdge);
            gl.TexParameteri(GlTexture2D, GlTextureWrapT, GlClampToEdge);
            gl.BindTexture(GlTexture2D, 0);

            _depthRenderbuffer = gl.GenRenderbuffer();
            gl.BindRenderbuffer(GlRenderbuffer, _depthRenderbuffer);
            gl.RenderbufferStorage(GlRenderbuffer, GlDepthComponent24, size.Width, size.Height);
            gl.BindRenderbuffer(GlRenderbuffer, 0);

            gl.BindFramebuffer(GlFramebuffer, _fbo);
            gl.FramebufferTexture2D(GlFramebuffer, GlColorAttachment0, GlTexture2D, _texture, 0);
            gl.FramebufferRenderbuffer(GlFramebuffer, GlDepthAttachment, GlRenderbuffer, _depthRenderbuffer);
            _ready = gl.CheckFramebufferStatus(GlFramebuffer) == GlFramebufferComplete;
            _size = size;
        }

        return _ready;
    }

    public void Composite(GlInterface gl, int targetFramebuffer, (int Width, int Height) size)
    {
        gl.BindFramebuffer(GlFramebuffer, targetFramebuffer);
        gl.Viewport(0, 0, size.Width, size.Height);
        gl.Disable(GlDepthTest);
        gl.Disable(GlBlend);
        gl.Disable(GlScissorTest);
        gl.Disable(GlCullFace);

        gl.UseProgram(_program);
        gl.ActiveTexture(GlTexture0);
        gl.BindTexture(GlTexture2D, _texture);
        gl.Uniform1i(gl.GetUniformLocationString(_program, "source"), 0);
        gl.BindVertexArray(_vao);
        gl.DrawArrays(GlTriangleStrip, 0, 4);
        gl.BindVertexArray(0);
        gl.BindTexture(GlTexture2D, 0);
        gl.UseProgram(0);
    }

    public void Release(GlInterface gl)
    {
        if (_texture != 0)
        {
            gl.DeleteTexture(_texture);
        }

        if (_depthRenderbuffer != 0)
        {
            gl.DeleteRenderbuffer(_depthRenderbuffer);
        }

        if (_fbo != 0)
        {
            gl.DeleteFramebuffer(_fbo);
        }

        if (_vao != 0)
        {
            gl.DeleteVertexArray(_vao);
        }

        if (_program != 0)
        {
            gl.DeleteProgram(_program);
        }

        _texture = _depthRenderbuffer = _fbo = _vao = _program = 0;
        _ready = false;
        _size = default;
    }
}
