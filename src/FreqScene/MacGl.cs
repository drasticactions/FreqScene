using System.Runtime.InteropServices;
using System.Text;

namespace FreqScene;

internal static partial class MacGl
{
    private const string OpenGl = "/System/Library/Frameworks/OpenGL.framework/OpenGL";

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

    [LibraryImport(OpenGl, EntryPoint = "glViewport")]
    public static partial void Viewport(int x, int y, int width, int height);

    [LibraryImport(OpenGl, EntryPoint = "glBindFramebuffer")]
    public static partial void BindFramebuffer(int target, uint framebuffer);

    [LibraryImport(OpenGl, EntryPoint = "glGenFramebuffers")]
    public static partial void GenFramebuffers(int count, out uint framebuffer);

    [LibraryImport(OpenGl, EntryPoint = "glDeleteFramebuffers")]
    public static partial void DeleteFramebuffers(int count, in uint framebuffer);

    [LibraryImport(OpenGl, EntryPoint = "glGenTextures")]
    public static partial void GenTextures(int count, out uint texture);

    [LibraryImport(OpenGl, EntryPoint = "glDeleteTextures")]
    public static partial void DeleteTextures(int count, in uint texture);

    [LibraryImport(OpenGl, EntryPoint = "glBindTexture")]
    public static partial void BindTexture(int target, uint texture);

    [LibraryImport(OpenGl, EntryPoint = "glTexImage2D")]
    public static partial void TexImage2D(
        int target, int level, int internalFormat, int width, int height, int border, int format, int type, IntPtr pixels);

    [LibraryImport(OpenGl, EntryPoint = "glTexParameteri")]
    public static partial void TexParameteri(int target, int parameter, int value);

    [LibraryImport(OpenGl, EntryPoint = "glFramebufferTexture2D")]
    public static partial void FramebufferTexture2D(int target, int attachment, int textureTarget, uint texture, int level);

    [LibraryImport(OpenGl, EntryPoint = "glGenRenderbuffers")]
    public static partial void GenRenderbuffers(int count, out uint renderbuffer);

    [LibraryImport(OpenGl, EntryPoint = "glDeleteRenderbuffers")]
    public static partial void DeleteRenderbuffers(int count, in uint renderbuffer);

    [LibraryImport(OpenGl, EntryPoint = "glBindRenderbuffer")]
    public static partial void BindRenderbuffer(int target, uint renderbuffer);

    [LibraryImport(OpenGl, EntryPoint = "glRenderbufferStorage")]
    public static partial void RenderbufferStorage(int target, int internalFormat, int width, int height);

    [LibraryImport(OpenGl, EntryPoint = "glFramebufferRenderbuffer")]
    public static partial void FramebufferRenderbuffer(int target, int attachment, int renderbufferTarget, uint renderbuffer);

    [LibraryImport(OpenGl, EntryPoint = "glCheckFramebufferStatus")]
    public static partial int CheckFramebufferStatus(int target);

    [LibraryImport(OpenGl, EntryPoint = "glBlitFramebuffer")]
    public static partial void BlitFramebuffer(
        int srcX0, int srcY0, int srcX1, int srcY1, int dstX0, int dstY0, int dstX1, int dstY1, int mask, int filter);

    [LibraryImport(OpenGl, EntryPoint = "glCreateShader")]
    public static partial uint CreateShader(int shaderType);

    [LibraryImport(OpenGl, EntryPoint = "glShaderSource")]
    private static unsafe partial void ShaderSourceRaw(uint shader, int count, byte** sources, int* lengths);

    [LibraryImport(OpenGl, EntryPoint = "glCompileShader")]
    public static partial void CompileShader(uint shader);

    [LibraryImport(OpenGl, EntryPoint = "glGetShaderiv")]
    public static partial void GetShaderiv(uint shader, int parameter, out int value);

    [LibraryImport(OpenGl, EntryPoint = "glGetShaderInfoLog")]
    private static partial void GetShaderInfoLog(uint shader, int maxLength, out int length, [Out] byte[] log);

    [LibraryImport(OpenGl, EntryPoint = "glCreateProgram")]
    public static partial uint CreateProgram();

    [LibraryImport(OpenGl, EntryPoint = "glAttachShader")]
    public static partial void AttachShader(uint program, uint shader);

    [LibraryImport(OpenGl, EntryPoint = "glLinkProgram")]
    public static partial void LinkProgram(uint program);

    [LibraryImport(OpenGl, EntryPoint = "glGetProgramiv")]
    public static partial void GetProgramiv(uint program, int parameter, out int value);

    [LibraryImport(OpenGl, EntryPoint = "glGetProgramInfoLog")]
    private static partial void GetProgramInfoLog(uint program, int maxLength, out int length, [Out] byte[] log);

    [LibraryImport(OpenGl, EntryPoint = "glDeleteShader")]
    public static partial void DeleteShader(uint shader);

    [LibraryImport(OpenGl, EntryPoint = "glDeleteProgram")]
    public static partial void DeleteProgram(uint program);

    [LibraryImport(OpenGl, EntryPoint = "glUseProgram")]
    public static partial void UseProgram(uint program);

    [LibraryImport(OpenGl, EntryPoint = "glGenVertexArrays")]
    public static partial void GenVertexArrays(int count, out uint array);

    [LibraryImport(OpenGl, EntryPoint = "glDeleteVertexArrays")]
    public static partial void DeleteVertexArrays(int count, in uint array);

    [LibraryImport(OpenGl, EntryPoint = "glBindVertexArray")]
    public static partial void BindVertexArray(uint array);

    [LibraryImport(OpenGl, EntryPoint = "glDrawArrays")]
    public static partial void DrawArrays(int mode, int first, int count);

    [LibraryImport(OpenGl, EntryPoint = "glActiveTexture")]
    public static partial void ActiveTexture(int texture);

    [LibraryImport(OpenGl, EntryPoint = "glUniform1i")]
    public static partial void Uniform1i(int location, int value);

    [LibraryImport(OpenGl, EntryPoint = "glGetUniformLocation", StringMarshalling = StringMarshalling.Utf8)]
    public static partial int GetUniformLocation(uint program, string name);

    [LibraryImport(OpenGl, EntryPoint = "glDisable")]
    public static partial void Disable(int capability);

    public static unsafe void ShaderSource(uint shader, string source)
    {
        var bytes = Encoding.UTF8.GetBytes(source);
        fixed (byte* p = bytes)
        {
            var ptr = p;
            var length = bytes.Length;
            ShaderSourceRaw(shader, 1, &ptr, &length);
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
            GetShaderInfoLog(shader, log.Length, out var length, log);
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
            GetProgramInfoLog(program, log.Length, out var length, log);
            DeleteProgram(program);
            throw new InvalidOperationException(
                $"Shader program link failed: {Encoding.UTF8.GetString(log, 0, Math.Max(0, length))}");
        }

        return program;
    }
}
