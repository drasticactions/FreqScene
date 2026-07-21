using System.Text;

namespace FreqScene;

internal static unsafe class Gl
{
    public const int Texture2D = 0x0DE1;
    public const int Rgba = 0x1908;
    public const int Rgba8 = 0x8058;
    public const int UnsignedByte = 0x1401;
    public const int TextureMinFilter = 0x2801;
    public const int TextureMagFilter = 0x2800;
    public const int Linear = 0x2601;
    public const int TextureWrapS = 0x2802;
    public const int TextureWrapT = 0x2803;
    public const int ClampToEdge = 0x812F;
    public const int Framebuffer = 0x8D40;
    public const int ReadFramebuffer = 0x8CA8;
    public const int DrawFramebuffer = 0x8CA9;
    public const int ColorAttachment0 = 0x8CE0;
    public const int DepthAttachment = 0x8D00;
    public const int Renderbuffer = 0x8D41;
    public const int DepthComponent24 = 0x81A6;
    public const int FramebufferComplete = 0x8CD5;
    public const int VertexShader = 0x8B31;
    public const int FragmentShader = 0x8B30;
    public const int CompileStatus = 0x8B81;
    public const int LinkStatus = 0x8B82;
    public const int TriangleStrip = 0x0005;
    public const int Texture0 = 0x84C0;
    public const int DepthTest = 0x0B71;
    public const int Blend = 0x0BE2;
    public const int ScissorTest = 0x0C11;
    public const int CullFace = 0x0B44;
    public const int ColorBufferBit = 0x4000;

    private static delegate* unmanaged<int, int, int, int, void> _viewport;
    private static delegate* unmanaged<int, uint, void> _bindFramebuffer;
    private static delegate* unmanaged<int, uint*, void> _genFramebuffers;
    private static delegate* unmanaged<int, uint*, void> _deleteFramebuffers;
    private static delegate* unmanaged<int, uint*, void> _genTextures;
    private static delegate* unmanaged<int, uint*, void> _deleteTextures;
    private static delegate* unmanaged<int, uint, void> _bindTexture;
    private static delegate* unmanaged<int, int, int, int, int, int, int, int, IntPtr, void> _texImage2D;
    private static delegate* unmanaged<int, int, int, void> _texParameteri;
    private static delegate* unmanaged<int, int, int, uint, int, void> _framebufferTexture2D;
    private static delegate* unmanaged<int, uint*, void> _genRenderbuffers;
    private static delegate* unmanaged<int, uint*, void> _deleteRenderbuffers;
    private static delegate* unmanaged<int, uint, void> _bindRenderbuffer;
    private static delegate* unmanaged<int, int, int, int, void> _renderbufferStorage;
    private static delegate* unmanaged<int, int, int, uint, void> _framebufferRenderbuffer;
    private static delegate* unmanaged<int, int> _checkFramebufferStatus;
    private static delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void> _blitFramebuffer;
    private static delegate* unmanaged<int, uint> _createShader;
    private static delegate* unmanaged<uint, int, byte**, int*, void> _shaderSource;
    private static delegate* unmanaged<uint, void> _compileShader;
    private static delegate* unmanaged<uint, int, int*, void> _getShaderiv;
    private static delegate* unmanaged<uint, int, int*, byte*, void> _getShaderInfoLog;
    private static delegate* unmanaged<uint> _createProgram;
    private static delegate* unmanaged<uint, uint, void> _attachShader;
    private static delegate* unmanaged<uint, void> _linkProgram;
    private static delegate* unmanaged<uint, int, int*, void> _getProgramiv;
    private static delegate* unmanaged<uint, int, int*, byte*, void> _getProgramInfoLog;
    private static delegate* unmanaged<uint, void> _deleteShader;
    private static delegate* unmanaged<uint, void> _deleteProgram;
    private static delegate* unmanaged<uint, void> _useProgram;
    private static delegate* unmanaged<int, uint*, void> _genVertexArrays;
    private static delegate* unmanaged<int, uint*, void> _deleteVertexArrays;
    private static delegate* unmanaged<uint, void> _bindVertexArray;
    private static delegate* unmanaged<int, int, int, void> _drawArrays;
    private static delegate* unmanaged<int, void> _activeTexture;
    private static delegate* unmanaged<int, int, void> _uniform1i;
    private static delegate* unmanaged<uint, byte*, int> _getUniformLocation;
    private static delegate* unmanaged<int, void> _disable;

    private static bool _initialized;

    public static void Initialize(Func<string, IntPtr> getProcAddress)
    {
        if (_initialized)
        {
            return;
        }

        _viewport = (delegate* unmanaged<int, int, int, int, void>)Load(getProcAddress, "glViewport");
        _bindFramebuffer = (delegate* unmanaged<int, uint, void>)Load(getProcAddress, "glBindFramebuffer");
        _genFramebuffers = (delegate* unmanaged<int, uint*, void>)Load(getProcAddress, "glGenFramebuffers");
        _deleteFramebuffers = (delegate* unmanaged<int, uint*, void>)Load(getProcAddress, "glDeleteFramebuffers");
        _genTextures = (delegate* unmanaged<int, uint*, void>)Load(getProcAddress, "glGenTextures");
        _deleteTextures = (delegate* unmanaged<int, uint*, void>)Load(getProcAddress, "glDeleteTextures");
        _bindTexture = (delegate* unmanaged<int, uint, void>)Load(getProcAddress, "glBindTexture");
        _texImage2D = (delegate* unmanaged<int, int, int, int, int, int, int, int, IntPtr, void>)Load(getProcAddress, "glTexImage2D");
        _texParameteri = (delegate* unmanaged<int, int, int, void>)Load(getProcAddress, "glTexParameteri");
        _framebufferTexture2D = (delegate* unmanaged<int, int, int, uint, int, void>)Load(getProcAddress, "glFramebufferTexture2D");
        _genRenderbuffers = (delegate* unmanaged<int, uint*, void>)Load(getProcAddress, "glGenRenderbuffers");
        _deleteRenderbuffers = (delegate* unmanaged<int, uint*, void>)Load(getProcAddress, "glDeleteRenderbuffers");
        _bindRenderbuffer = (delegate* unmanaged<int, uint, void>)Load(getProcAddress, "glBindRenderbuffer");
        _renderbufferStorage = (delegate* unmanaged<int, int, int, int, void>)Load(getProcAddress, "glRenderbufferStorage");
        _framebufferRenderbuffer = (delegate* unmanaged<int, int, int, uint, void>)Load(getProcAddress, "glFramebufferRenderbuffer");
        _checkFramebufferStatus = (delegate* unmanaged<int, int>)Load(getProcAddress, "glCheckFramebufferStatus");
        _blitFramebuffer = (delegate* unmanaged<int, int, int, int, int, int, int, int, int, int, void>)Load(getProcAddress, "glBlitFramebuffer");
        _createShader = (delegate* unmanaged<int, uint>)Load(getProcAddress, "glCreateShader");
        _shaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)Load(getProcAddress, "glShaderSource");
        _compileShader = (delegate* unmanaged<uint, void>)Load(getProcAddress, "glCompileShader");
        _getShaderiv = (delegate* unmanaged<uint, int, int*, void>)Load(getProcAddress, "glGetShaderiv");
        _getShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Load(getProcAddress, "glGetShaderInfoLog");
        _createProgram = (delegate* unmanaged<uint>)Load(getProcAddress, "glCreateProgram");
        _attachShader = (delegate* unmanaged<uint, uint, void>)Load(getProcAddress, "glAttachShader");
        _linkProgram = (delegate* unmanaged<uint, void>)Load(getProcAddress, "glLinkProgram");
        _getProgramiv = (delegate* unmanaged<uint, int, int*, void>)Load(getProcAddress, "glGetProgramiv");
        _getProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Load(getProcAddress, "glGetProgramInfoLog");
        _deleteShader = (delegate* unmanaged<uint, void>)Load(getProcAddress, "glDeleteShader");
        _deleteProgram = (delegate* unmanaged<uint, void>)Load(getProcAddress, "glDeleteProgram");
        _useProgram = (delegate* unmanaged<uint, void>)Load(getProcAddress, "glUseProgram");
        _genVertexArrays = (delegate* unmanaged<int, uint*, void>)Load(getProcAddress, "glGenVertexArrays");
        _deleteVertexArrays = (delegate* unmanaged<int, uint*, void>)Load(getProcAddress, "glDeleteVertexArrays");
        _bindVertexArray = (delegate* unmanaged<uint, void>)Load(getProcAddress, "glBindVertexArray");
        _drawArrays = (delegate* unmanaged<int, int, int, void>)Load(getProcAddress, "glDrawArrays");
        _activeTexture = (delegate* unmanaged<int, void>)Load(getProcAddress, "glActiveTexture");
        _uniform1i = (delegate* unmanaged<int, int, void>)Load(getProcAddress, "glUniform1i");
        _getUniformLocation = (delegate* unmanaged<uint, byte*, int>)Load(getProcAddress, "glGetUniformLocation");
        _disable = (delegate* unmanaged<int, void>)Load(getProcAddress, "glDisable");
        _initialized = true;
    }

    private static IntPtr Load(Func<string, IntPtr> getProcAddress, string name)
    {
        var pointer = getProcAddress(name);
        if (pointer == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenGL function {name} could not be resolved.");
        }

        return pointer;
    }

    public static void Viewport(int x, int y, int width, int height) => _viewport(x, y, width, height);

    public static void BindFramebuffer(int target, uint framebuffer) => _bindFramebuffer(target, framebuffer);

    public static void GenFramebuffers(int count, out uint framebuffer)
    {
        fixed (uint* p = &framebuffer)
        {
            _genFramebuffers(count, p);
        }
    }

    public static void DeleteFramebuffers(int count, in uint framebuffer)
    {
        fixed (uint* p = &framebuffer)
        {
            _deleteFramebuffers(count, p);
        }
    }

    public static void GenTextures(int count, out uint texture)
    {
        fixed (uint* p = &texture)
        {
            _genTextures(count, p);
        }
    }

    public static void DeleteTextures(int count, in uint texture)
    {
        fixed (uint* p = &texture)
        {
            _deleteTextures(count, p);
        }
    }

    public static void BindTexture(int target, uint texture) => _bindTexture(target, texture);

    public static void TexImage2D(
        int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr pixels) =>
        _texImage2D(target, level, internalFormat, width, height, border, format, type, pixels);

    public static void TexParameteri(int target, int parameter, int value) => _texParameteri(target, parameter, value);

    public static void FramebufferTexture2D(int target, int attachment, int textureTarget, uint texture, int level) =>
        _framebufferTexture2D(target, attachment, textureTarget, texture, level);

    public static void GenRenderbuffers(int count, out uint renderbuffer)
    {
        fixed (uint* p = &renderbuffer)
        {
            _genRenderbuffers(count, p);
        }
    }

    public static void DeleteRenderbuffers(int count, in uint renderbuffer)
    {
        fixed (uint* p = &renderbuffer)
        {
            _deleteRenderbuffers(count, p);
        }
    }

    public static void BindRenderbuffer(int target, uint renderbuffer) => _bindRenderbuffer(target, renderbuffer);

    public static void RenderbufferStorage(int target, int internalFormat, int width, int height) =>
        _renderbufferStorage(target, internalFormat, width, height);

    public static void FramebufferRenderbuffer(int target, int attachment, int renderbufferTarget, uint renderbuffer) =>
        _framebufferRenderbuffer(target, attachment, renderbufferTarget, renderbuffer);

    public static int CheckFramebufferStatus(int target) => _checkFramebufferStatus(target);

    public static void BlitFramebuffer(
        int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, int mask, int filter) =>
        _blitFramebuffer(srcX0, srcY0, srcX1, srcY1, dstX0, dstY0, dstX1, dstY1, mask, filter);

    public static uint CreateShader(int shaderType) => _createShader(shaderType);

    public static void CompileShader(uint shader) => _compileShader(shader);

    public static void GetShaderiv(uint shader, int parameter, out int value)
    {
        fixed (int* p = &value)
        {
            _getShaderiv(shader, parameter, p);
        }
    }

    public static uint CreateProgram() => _createProgram();

    public static void AttachShader(uint program, uint shader) => _attachShader(program, shader);

    public static void LinkProgram(uint program) => _linkProgram(program);

    public static void GetProgramiv(uint program, int parameter, out int value)
    {
        fixed (int* p = &value)
        {
            _getProgramiv(program, parameter, p);
        }
    }

    public static void DeleteShader(uint shader) => _deleteShader(shader);

    public static void DeleteProgram(uint program) => _deleteProgram(program);

    public static void UseProgram(uint program) => _useProgram(program);

    public static void GenVertexArrays(int count, out uint array)
    {
        fixed (uint* p = &array)
        {
            _genVertexArrays(count, p);
        }
    }

    public static void DeleteVertexArrays(int count, in uint array)
    {
        fixed (uint* p = &array)
        {
            _deleteVertexArrays(count, p);
        }
    }

    public static void BindVertexArray(uint array) => _bindVertexArray(array);

    public static void DrawArrays(int mode, int first, int count) => _drawArrays(mode, first, count);

    public static void ActiveTexture(int texture) => _activeTexture(texture);

    public static void Uniform1i(int location, int value) => _uniform1i(location, value);

    public static int GetUniformLocation(uint program, string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            return _getUniformLocation(program, p);
        }
    }

    public static void Disable(int capability) => _disable(capability);

    public static void ShaderSource(uint shader, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* p = bytes)
        {
            var pointer = p;
            var length = bytes.Length;
            _shaderSource(shader, 1, &pointer, &length);
        }
    }

    public static uint CompileShaderChecked(int shaderType, string source)
    {
        var shader = CreateShader(shaderType);
        ShaderSource(shader, source);
        CompileShader(shader);
        GetShaderiv(shader, CompileStatus, out var status);
        if (status == 0)
        {
            var log = new byte[4096];
            int length;
            fixed (byte* p = log)
            {
                _getShaderInfoLog(shader, log.Length, &length, p);
            }

            DeleteShader(shader);
            throw new InvalidOperationException(
                $"Shader compilation failed: {Encoding.UTF8.GetString(log, 0, Math.Max(0, length))}");
        }

        return shader;
    }

    public static uint LinkProgramChecked(uint vertexShader, uint fragmentShader)
    {
        var program = CreateProgram();
        AttachShader(program, vertexShader);
        AttachShader(program, fragmentShader);
        LinkProgram(program);
        GetProgramiv(program, LinkStatus, out var status);
        if (status == 0)
        {
            var log = new byte[4096];
            int length;
            fixed (byte* p = log)
            {
                _getProgramInfoLog(program, log.Length, &length, p);
            }

            DeleteProgram(program);
            throw new InvalidOperationException(
                $"Shader program link failed: {Encoding.UTF8.GetString(log, 0, Math.Max(0, length))}");
        }

        return program;
    }
}
