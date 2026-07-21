using ProjectMDotNet;

namespace FreqScene;

internal sealed unsafe class GlFramePipeline
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

    private const string WallpaperFragmentSource = """
        #version 330 core
        in vec2 uv;
        out vec4 fragColor;
        uniform sampler2D source;
        uniform sampler2D wallpaper;
        uniform vec4 wallpaperTransform; // xy = scale, zw = offset (top-left-origin uv)
        uniform vec4 wallpaperColor;
        uniform int wallpaperMode;       // 0 = image, 1 = tiled image, 2 = color only
        void main()
        {
            vec3 color = texture(source, uv).rgb;
            float alpha = max(color.r, max(color.g, color.b));
            vec2 wuv = vec2(uv.x, 1.0 - uv.y) * wallpaperTransform.xy + wallpaperTransform.zw;
            vec3 background = wallpaperColor.rgb;
            if (wallpaperMode == 1)
            {
                background = texture(wallpaper, fract(wuv)).rgb;
            }
            else if (wallpaperMode == 0 &&
                     wuv.x >= 0.0 && wuv.x <= 1.0 && wuv.y >= 0.0 && wuv.y <= 1.0)
            {
                background = texture(wallpaper, wuv).rgb;
            }

            fragColor = vec4(color + background * (1.0 - alpha), 1.0);
        }
        """;

    private (int Width, int Height) _lastWindowSize;
    private uint _fbo;
    private uint _fboTexture;
    private uint _fboDepth;
    private (int Width, int Height) _fboSize;
    private uint _program;
    private uint _wallpaperProgram;
    private uint _wallpaperTexture;
    private WallpaperBackground? _wallpaper;
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

    public void SetWallpaperBackground(WallpaperBackground? background)
    {
        _wallpaper = background;
        if (background is { BgraPixels: { } pixels, ImageWidth: > 0, ImageHeight: > 0 })
        {
            if (_wallpaperTexture == 0)
            {
                Gl.GenTextures(1, out _wallpaperTexture);
            }

            var wrap = background.Position == WallpaperPosition.Tile ? Gl.Repeat : Gl.ClampToEdge;
            Gl.BindTexture(Gl.Texture2D, _wallpaperTexture);
            fixed (byte* p = pixels)
            {
                Gl.TexImage2D(
                    Gl.Texture2D, 0, Gl.Rgba8, background.ImageWidth, background.ImageHeight,
                    0, Gl.Bgra, Gl.UnsignedByte, (IntPtr)p);
            }

            Gl.TexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.Linear);
            Gl.TexParameteri(Gl.Texture2D, Gl.TextureMagFilter, Gl.Linear);
            Gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapS, wrap);
            Gl.TexParameteri(Gl.Texture2D, Gl.TextureWrapT, wrap);
            Gl.BindTexture(Gl.Texture2D, 0);
        }
        else if (_wallpaperTexture != 0)
        {
            Gl.DeleteTextures(1, in _wallpaperTexture);
            _wallpaperTexture = 0;
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

        if (_wallpaperTexture != 0)
        {
            Gl.DeleteTextures(1, in _wallpaperTexture);
            _wallpaperTexture = 0;
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

        if (_wallpaperProgram != 0)
        {
            Gl.DeleteProgram(_wallpaperProgram);
            _wallpaperProgram = 0;
        }

        _wallpaper = null;
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
        if (_vao == 0)
        {
            Gl.GenVertexArrays(1, out _vao);
        }

        Gl.BindFramebuffer(Gl.Framebuffer, 0);
        Gl.Viewport(0, 0, width, height);
        Gl.Disable(Gl.DepthTest);
        Gl.Disable(Gl.Blend);
        Gl.Disable(Gl.ScissorTest);
        Gl.Disable(Gl.CullFace);

        if (_wallpaper is { } wallpaper)
        {
            if (_wallpaperProgram == 0)
            {
                _wallpaperProgram = BuildProgram(WallpaperFragmentSource);
            }

            Gl.UseProgram(_wallpaperProgram);
            Gl.ActiveTexture(Gl.Texture1);
            Gl.BindTexture(Gl.Texture2D, _wallpaperTexture);
            Gl.Uniform1i(Gl.GetUniformLocation(_wallpaperProgram, "wallpaper"), 1);
            Gl.ActiveTexture(Gl.Texture0);
            Gl.BindTexture(Gl.Texture2D, _fboTexture);
            Gl.Uniform1i(Gl.GetUniformLocation(_wallpaperProgram, "source"), 0);

            ComputeWallpaperPlacement(
                wallpaper, width, height,
                out var scaleX, out var scaleY, out var offsetX, out var offsetY, out var mode);
            Gl.Uniform4f(
                Gl.GetUniformLocation(_wallpaperProgram, "wallpaperTransform"),
                scaleX, scaleY, offsetX, offsetY);
            Gl.Uniform4f(
                Gl.GetUniformLocation(_wallpaperProgram, "wallpaperColor"),
                wallpaper.BackgroundRed, wallpaper.BackgroundGreen, wallpaper.BackgroundBlue, 1f);
            Gl.Uniform1i(Gl.GetUniformLocation(_wallpaperProgram, "wallpaperMode"), mode);

            Draw();
            Gl.ActiveTexture(Gl.Texture1);
            Gl.BindTexture(Gl.Texture2D, 0);
            Gl.ActiveTexture(Gl.Texture0);
        }
        else
        {
            if (_program == 0)
            {
                _program = BuildProgram(FragmentSource);
            }

            Gl.UseProgram(_program);
            Gl.ActiveTexture(Gl.Texture0);
            Gl.BindTexture(Gl.Texture2D, _fboTexture);
            Gl.Uniform1i(Gl.GetUniformLocation(_program, "source"), 0);
            Draw();
        }

        Gl.BindTexture(Gl.Texture2D, 0);
        Gl.UseProgram(0);
    }

    private void Draw()
    {
        Gl.BindVertexArray(_vao);
        Gl.DrawArrays(Gl.TriangleStrip, 0, 4);
        Gl.BindVertexArray(0);
    }

    private void ComputeWallpaperPlacement(
        WallpaperBackground wallpaper, int width, int height,
        out float scaleX, out float scaleY, out float offsetX, out float offsetY, out int mode)
    {
        if (wallpaper.BgraPixels is null || _wallpaperTexture == 0)
        {
            scaleX = 1f;
            scaleY = 1f;
            offsetX = 0f;
            offsetY = 0f;
            mode = 2;
            return;
        }

        // The destination rectangle (in window pixels, top-left origin) that the image
        // maps onto, mirroring how the shell lays out each wallpaper position.
        double iw = wallpaper.ImageWidth;
        double ih = wallpaper.ImageHeight;
        double dx = 0, dy = 0, dw = width, dh = height;
        switch (wallpaper.Position)
        {
            case WallpaperPosition.Center:
                dw = iw;
                dh = ih;
                dx = (width - iw) / 2;
                dy = (height - ih) / 2;
                break;

            case WallpaperPosition.Tile:
                dw = iw;
                dh = ih;
                break;

            case WallpaperPosition.Stretch:
                break;

            case WallpaperPosition.Fit:
            {
                var scale = Math.Min(width / iw, height / ih);
                dw = iw * scale;
                dh = ih * scale;
                dx = (width - dw) / 2;
                dy = (height - dh) / 2;
                break;
            }

            case WallpaperPosition.Span when wallpaper.SpanWidth > 0 && wallpaper.SpanHeight > 0:
            {
                var scale = Math.Max(wallpaper.SpanWidth / iw, wallpaper.SpanHeight / ih);
                dw = iw * scale;
                dh = ih * scale;
                dx = wallpaper.SpanX + (wallpaper.SpanWidth - dw) / 2;
                dy = wallpaper.SpanY + (wallpaper.SpanHeight - dh) / 2;
                break;
            }

            default: // Fill
            {
                var scale = Math.Max(width / iw, height / ih);
                dw = iw * scale;
                dh = ih * scale;
                dx = (width - dw) / 2;
                dy = (height - dh) / 2;
                break;
            }
        }

        scaleX = (float)(width / dw);
        scaleY = (float)(height / dh);
        offsetX = (float)(-dx / dw);
        offsetY = (float)(-dy / dh);
        mode = wallpaper.Position == WallpaperPosition.Tile ? 1 : 0;
    }

    private uint BuildProgram(string fragmentSource)
    {
        var vertex = Gl.CompileShaderChecked(Gl.VertexShader, VertexSource);
        var fragment = Gl.CompileShaderChecked(Gl.FragmentShader, fragmentSource);
        var program = Gl.LinkProgramChecked(vertex, fragment);
        Gl.DeleteShader(vertex);
        Gl.DeleteShader(fragment);
        return program;
    }
}
