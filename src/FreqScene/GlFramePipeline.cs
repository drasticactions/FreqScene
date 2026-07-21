using ProjectMDotNet;

namespace FreqScene;

internal sealed class GlFramePipeline
{
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

    private (int Width, int Height) _lastWindowSize;
    private uint _fbo;
    private uint _fboTexture;
    private uint _fboDepth;
    private (int Width, int Height) _fboSize;
    private uint _program;
    private uint _vao;

    public void ResetWindowSize() => _lastWindowSize = default;

    public void Render(ProjectM instance, int width, int height, double renderScale, bool transparent)
    {
        var useOffscreen = transparent || renderScale < 0.999;
        if (useOffscreen)
        {
            var scaled = (Math.Max(1, (int)(width * renderScale)), Math.Max(1, (int)(height * renderScale)));
            EnsureOffscreen(scaled);
            SetWindowSize(instance, scaled);
            Gl.BindFramebuffer(Gl.Framebuffer, _fbo);
            Gl.Viewport(0, 0, scaled.Item1, scaled.Item2);
            instance.RenderFrame(_fbo);

            if (transparent)
            {
                Composite(width, height);
            }
            else
            {
                Gl.BindFramebuffer(Gl.ReadFramebuffer, _fbo);
                Gl.BindFramebuffer(Gl.DrawFramebuffer, 0);
                Gl.BlitFramebuffer(
                    0, 0, scaled.Item1, scaled.Item2, 0, 0, width, height, Gl.ColorBufferBit, Gl.Linear);
            }

            Gl.BindFramebuffer(Gl.Framebuffer, 0);
        }
        else
        {
            SetWindowSize(instance, (width, height));
            Gl.BindFramebuffer(Gl.Framebuffer, 0);
            Gl.Viewport(0, 0, width, height);
            instance.RenderFrame(0);
        }
    }

    public void Release()
    {
        if (_fboTexture != 0)
        {
            Gl.DeleteTextures(1, in _fboTexture);
            _fboTexture = 0;
        }

        if (_fboDepth != 0)
        {
            Gl.DeleteRenderbuffers(1, in _fboDepth);
            _fboDepth = 0;
        }

        if (_fbo != 0)
        {
            Gl.DeleteFramebuffers(1, in _fbo);
            _fbo = 0;
        }

        if (_vao != 0)
        {
            Gl.DeleteVertexArrays(1, in _vao);
            _vao = 0;
        }

        if (_program != 0)
        {
            Gl.DeleteProgram(_program);
            _program = 0;
        }

        _fboSize = default;
        _lastWindowSize = default;
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
            Gl.GenFramebuffers(1, out _fbo);
        }

        if (_fboSize == size)
        {
            return;
        }

        if (_fboTexture != 0)
        {
            Gl.DeleteTextures(1, in _fboTexture);
        }

        if (_fboDepth != 0)
        {
            Gl.DeleteRenderbuffers(1, in _fboDepth);
        }

        Gl.GenTextures(1, out _fboTexture);
        Gl.BindTexture(Gl.Texture2D, _fboTexture);
        Gl.TexImage2D(Gl.Texture2D, 0, Gl.Rgba8, size.Width, size.Height, 0, Gl.Rgba, Gl.UnsignedByte, IntPtr.Zero);
        Gl.TexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.Linear);
        Gl.TexParameteri(Gl.Texture2D, Gl.TextureMagFilter, Gl.Linear);
        Gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapS, Gl.ClampToEdge);
        Gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapT, Gl.ClampToEdge);
        Gl.BindTexture(Gl.Texture2D, 0);

        Gl.GenRenderbuffers(1, out _fboDepth);
        Gl.BindRenderbuffer(Gl.Renderbuffer, _fboDepth);
        Gl.RenderbufferStorage(Gl.Renderbuffer, Gl.DepthComponent24, size.Width, size.Height);
        Gl.BindRenderbuffer(Gl.Renderbuffer, 0);

        Gl.BindFramebuffer(Gl.Framebuffer, _fbo);
        Gl.FramebufferTexture2D(Gl.Framebuffer, Gl.ColorAttachment0, Gl.Texture2D, _fboTexture, 0);
        Gl.FramebufferRenderbuffer(Gl.Framebuffer, Gl.DepthAttachment, Gl.Renderbuffer, _fboDepth);
        var status = Gl.CheckFramebufferStatus(Gl.Framebuffer);
        Gl.BindFramebuffer(Gl.Framebuffer, 0);
        if (status != Gl.FramebufferComplete)
        {
            throw new InvalidOperationException($"Offscreen framebuffer incomplete: 0x{status:X}");
        }

        _fboSize = size;
    }

    private void Composite(int width, int height)
    {
        if (_program == 0)
        {
            var vertex = Gl.CompileShaderChecked(Gl.VertexShader, VertexSource);
            var fragment = Gl.CompileShaderChecked(Gl.FragmentShader, FragmentSource);
            _program = Gl.LinkProgramChecked(vertex, fragment);
            Gl.DeleteShader(vertex);
            Gl.DeleteShader(fragment);
            Gl.GenVertexArrays(1, out _vao);
        }

        Gl.BindFramebuffer(Gl.Framebuffer, 0);
        Gl.Viewport(0, 0, width, height);
        Gl.Disable(Gl.DepthTest);
        Gl.Disable(Gl.Blend);
        Gl.Disable(Gl.ScissorTest);
        Gl.Disable(Gl.CullFace);

        Gl.UseProgram(_program);
        Gl.ActiveTexture(Gl.Texture0);
        Gl.BindTexture(Gl.Texture2D, _fboTexture);
        Gl.Uniform1i(Gl.GetUniformLocation(_program, "source"), 0);
        Gl.BindVertexArray(_vao);
        Gl.DrawArrays(Gl.TriangleStrip, 0, 4);
        Gl.BindVertexArray(0);
        Gl.BindTexture(Gl.Texture2D, 0);
        Gl.UseProgram(0);
    }
}
