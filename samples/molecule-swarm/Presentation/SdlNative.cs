using System.Runtime.InteropServices;

namespace MoleculeSwarm;

/// <summary>SDL2 window, OpenGL context and event plumbing, imported straight from libSDL2.</summary>
internal static class Sdl
{
    public const uint InitVideo = 0x00000020;
    public const uint WindowShown = 0x00000004;
    public const uint WindowOpenGl = 0x00000002;
    public const uint WindowHidden = 0x00000008;
    public const int WindowPosCentered = 0x2FFF0000;

    public const uint QuitEvent = 0x100;
    public const uint KeyDownEvent = 0x300;
    public const uint KeyUpEvent = 0x301;
    public const uint MouseButtonDownEvent = 0x401;
    public const uint MouseMotionEvent = 0x400;
    public const uint MouseWheelEvent = 0x403;

    private const string Library = "libSDL2-2.0.so.0";

    public static string Error() => Marshal.PtrToStringAnsi(SDL_GetError()) ?? "unknown error";

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_Init(uint flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_CreateWindow(string title, int x, int y, int width, int height, uint flags);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GL_SetAttribute(int attribute, int value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr SDL_GL_CreateContext(IntPtr window);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GL_SetSwapInterval(int interval);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_GL_SwapWindow(IntPtr window);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_GL_DeleteContext(IntPtr context);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_PollEvent(IntPtr @event);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SDL_GetMouseState(out int x, out int y);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_SetWindowTitle(IntPtr window, string title);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_DestroyWindow(IntPtr window);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    public static extern void SDL_Quit();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SDL_GetError();
}
