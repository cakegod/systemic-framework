using System.Runtime.InteropServices;
using System.Text;

namespace MoleculeSwarm;

/// <summary>
/// Raw OpenGL 3.3 entry points and constants, imported straight from libGL. There is no wrapper
/// layer on purpose: every call here is one call onto the driver.
/// </summary>
internal static class Gl
{
    public const uint ColorBufferBit = 0x4000;
    public const uint Triangles = 0x0004;
    public const uint Points = 0x0000;
    public const uint Float = 0x1406;
    public const uint UnsignedByte = 0x1401;
    public const uint ArrayBuffer = 0x8892;
    public const uint DynamicDraw = 0x88E8;
    public const uint StaticDraw = 0x88E4;
    public const uint VertexShader = 0x8B31;
    public const uint FragmentShader = 0x8B30;
    public const uint CompileStatus = 0x8B81;
    public const uint LinkStatus = 0x8B82;
    public const uint Blend = 0x0BE2;
    public const uint SrcAlpha = 0x0302;
    public const uint OneMinusSrcAlpha = 0x0303;
    public const uint One = 0x0001;
    public const uint ProgramPointSize = 0x8642;
    public const uint PointSprite = 0x8861;
    public const uint DepthTest = 0x0B71;
    public const uint Renderer = 0x1F01;
    public const uint Back = 0x0405;
    public const uint Rgba = 0x1908;
    public const uint Rgba8 = 0x8058;
    public const uint Texture2D = 0x0DE1;
    public const uint Texture0 = 0x84C0;
    public const uint TextureMinFilter = 0x2801;
    public const uint TextureMagFilter = 0x2800;
    public const uint TextureWrapS = 0x2802;
    public const uint TextureWrapT = 0x2803;
    public const int Nearest = 0x2600;
    public const int Linear = 0x2601;
    public const int ClampToEdge = 0x812F;
    public const uint Framebuffer = 0x8D40;
    public const uint ColorAttachment0 = 0x8CE0;
    public const uint FramebufferComplete = 0x8CD5;

    private const string Library = "libGL.so.1";

    public static uint BuildProgram(string vertex, string fragment)
    {
        var program = glCreateProgram();
        Attach(program, VertexShader, vertex);
        Attach(program, FragmentShader, fragment);
        glLinkProgram(program);
        glGetProgramiv(program, LinkStatus, out var linked);
        if (linked == 0)
        {
            throw new InvalidOperationException($"Shader link failed: {Info(program, isProgram: true)}");
        }

        return program;

        static void Attach(uint program, uint type, string source)
        {
            var shader = glCreateShader(type);
            var pointer = Marshal.StringToHGlobalAnsi(source);
            try
            {
                glShaderSource(shader, 1, [pointer], IntPtr.Zero);
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }

            glCompileShader(shader);
            glGetShaderiv(shader, CompileStatus, out var compiled);
            if (compiled == 0)
            {
                throw new InvalidOperationException($"Shader compile failed: {Info(shader, isProgram: false)}");
            }

            glAttachShader(program, shader);
        }

        static string Info(uint handle, bool isProgram)
        {
            var log = new StringBuilder(4096);
            if (isProgram)
            {
                glGetProgramInfoLog(handle, 4096, out _, log);
            }
            else
            {
                glGetShaderInfoLog(handle, 4096, out _, log);
            }

            return log.ToString();
        }
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glClearColor(float red, float green, float blue, float alpha);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glClear(uint mask);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glEnable(uint capability);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glDisable(uint capability);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glBlendFunc(uint source, uint destination);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glGenVertexArrays(int count, uint[] arrays);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glBindVertexArray(uint array);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glGenBuffers(int count, uint[] buffers);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glBindBuffer(uint target, uint buffer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glBufferData(uint target, IntPtr size, IntPtr data, uint usage);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glBufferSubData(uint target, IntPtr offset, IntPtr size, IntPtr data);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glEnableVertexAttribArray(uint index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glVertexAttribPointer(uint index, int size, uint type, bool normalized,
        int stride, IntPtr pointer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glDrawArrays(uint mode, int first, int count);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glGenTextures(int count, uint[] textures);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glBindTexture(uint target, uint texture);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glTexImage2D(uint target, int level, int internalFormat, int width, int height,
        int border, uint format, uint type, IntPtr pixels);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glTexParameteri(uint target, uint name, int param);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glActiveTexture(uint texture);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glGenFramebuffers(int count, uint[] framebuffers);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glBindFramebuffer(uint target, uint framebuffer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glFramebufferTexture2D(uint target, uint attachment, uint textureTarget,
        uint texture, int level);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint glCheckFramebufferStatus(uint target);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glViewport(int x, int y, int width, int height);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glDeleteFramebuffers(int count, uint[] framebuffers);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint glCreateShader(uint type);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glShaderSource(uint shader, int count, IntPtr[] sources, IntPtr lengths);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glCompileShader(uint shader);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glGetShaderiv(uint shader, uint name, out int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glGetShaderInfoLog(uint shader, int bufferSize, out int length, StringBuilder log);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint glCreateProgram();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glAttachShader(uint program, uint shader);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glLinkProgram(uint program);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glGetProgramiv(uint program, uint name, out int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glGetProgramInfoLog(uint program, int bufferSize, out int length, StringBuilder log);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glUseProgram(uint program);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int glGetUniformLocation(uint program, string name);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glUniform1i(int location, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glUniform1f(int location, float value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glUniform2f(int location, float x, float y);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glUniform3f(int location, float x, float y, float z);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glUniform4f(int location, float x, float y, float z, float w);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glReadBuffer(uint mode);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void glReadPixels(int x, int y, int width, int height, uint format, uint type,
        IntPtr pixels);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr glGetString(uint name);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint glGetError();
}
