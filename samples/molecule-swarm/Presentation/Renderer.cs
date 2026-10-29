using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace MoleculeSwarm;

/// <summary>
/// The window and the draw order. It owns the GL context, the shared triangle buffer, and the four
/// passes; an SDL window that is hidden still gets a real context, which is how the screenshot mode
/// renders without a display.
/// </summary>
internal sealed class Renderer : IDisposable
{
    public const int Width = Sim.Width;
    public const int Height = Sim.Height;

    private const int ScancodeEscape = 41;
    private const int ScancodeQuit = 20;

    private readonly IntPtr _window;
    private readonly IntPtr _context;

    private readonly uint _shapeProgram;
    private readonly int _shapeViewport;
    private readonly int _shapeCamera;
    private readonly uint _shapeBuffer;
    private readonly uint _shapeVao;

    private readonly ShapeBatch _shapes = new();
    private readonly GroundPass _ground = new();
    private readonly SpritePass _sprites = new();
    private readonly WorldPass _world = new();
    private readonly Hud _hud = new();
    private readonly BloomPass _bloom = new();

    private Renderer(IntPtr window, IntPtr context)
    {
        _window = window;
        _context = context;

        _shapeProgram = Gl.BuildProgram(Shaders.ShapeVertex, Shaders.ShapeFragment);
        _shapeViewport = Gl.glGetUniformLocation(_shapeProgram, "uViewport");
        _shapeCamera = Gl.glGetUniformLocation(_shapeProgram, "uCamera");

        var buffers = new uint[1];
        Gl.glGenBuffers(1, buffers);
        _shapeBuffer = buffers[0];
        var arrays = new uint[1];
        Gl.glGenVertexArrays(1, arrays);
        _shapeVao = arrays[0];

        Gl.glBindVertexArray(_shapeVao);
        Gl.glBindBuffer(Gl.ArrayBuffer, _shapeBuffer);
        Gl.glBufferData(Gl.ArrayBuffer, (IntPtr)(ShapeBatch.MaxVertices * 4L), IntPtr.Zero, Gl.DynamicDraw);
        Gl.glEnableVertexAttribArray(0);
        Gl.glVertexAttribPointer(0, 2, Gl.Float, false, 24, IntPtr.Zero);
        Gl.glEnableVertexAttribArray(1);
        Gl.glVertexAttribPointer(1, 4, Gl.Float, false, 24, (IntPtr)8);

        var backend = Marshal.PtrToStringAnsi(Gl.glGetString(Gl.Renderer)) ?? "unknown";
        Backend = backend;
        Gl.glEnable(Gl.ProgramPointSize);
        // Some drivers (radeonsi among them) still gate gl_PointCoord behind the legacy point
        // sprite enable, even in a 3.3 profile. Without it every point sprite samples (0, 0) and
        // a shader that shapes the sprite discards the whole thing.
        Gl.glEnable(Gl.PointSprite);
        Gl.glDisable(Gl.DepthTest);
        Gl.glEnable(Gl.Blend);
        Gl.glBlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
        Gl.glClearColor(0.10f, 0.14f, 0.14f, 1f);
    }

    public string Backend { get; }

    public static Renderer Open(string title, bool hidden)
    {
        if (Sdl.SDL_Init(Sdl.InitVideo) != 0)
        {
            throw new InvalidOperationException($"SDL_Init failed: {Sdl.Error()}");
        }

        Sdl.SDL_GL_SetAttribute(0x0008, 3); // major
        Sdl.SDL_GL_SetAttribute(0x0009, 3); // minor
        Sdl.SDL_GL_SetAttribute(0x0010, 0x0001); // core profile
        Sdl.SDL_GL_SetAttribute(0x0005, 1); // double buffer
        Sdl.SDL_GL_SetAttribute(0x0006, 0); // depth size

        var flags = Sdl.WindowOpenGl | (hidden ? Sdl.WindowHidden : Sdl.WindowShown);
        var window = Sdl.SDL_CreateWindow(title, Sdl.WindowPosCentered, Sdl.WindowPosCentered, Width, Height, flags);
        if (window == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SDL_CreateWindow failed: {Sdl.Error()}");
        }

        var context = Sdl.SDL_GL_CreateContext(window);
        if (context == IntPtr.Zero)
        {
            throw new InvalidOperationException($"SDL_GL_CreateContext failed: {Sdl.Error()}");
        }

        Sdl.SDL_GL_SetSwapInterval(1);
        return new Renderer(window, context);
    }

    /// <summary>Draws one frame of the game. Call <see cref="Present"/> to show it.</summary>
    public void Frame(Sim sim, int selectedKind, int hoveredMount, bool paused, bool gameOver,
        Camera camera, Vector2 mouse)
    {
        _shapes.Reset();
        _sprites.Camera = camera;
        _bloom.Begin();
        Gl.glClear(Gl.ColorBufferBit);

        _ground.Draw(sim.Terrain, camera);
        _sprites.DrawSplats(sim.Effects);
        _sprites.DrawOrcs(sim.Swarm, sim.Elapsed);

        _world.Draw(sim, _shapes, hoveredMount, selectedKind, mouse);
        FlushShapes(camera);
        _sprites.DrawGlows(sim.Effects);

        // Everything above is the scene; the HUD comes after the bloom so the counters stay crisp.
        _bloom.Resolve();

        _hud.Draw(sim, _shapes, selectedKind, paused, gameOver);
        FlushShapes(Camera.Default);
    }

    public void Present() => Sdl.SDL_GL_SwapWindow(_window);

    public void RenderOnly(Sim sim) =>
        Frame(sim, 0, -1, paused: false, gameOver: sim.GameOver, Camera.Default,
            new Vector2(-64f, -64f));

    public void SetTitle(string title) => Sdl.SDL_SetWindowTitle(_window, title);

    public void Pump(InputState input)
    {
        var buffer = Marshal.AllocHGlobal(64);
        try
        {
            while (Sdl.SDL_PollEvent(buffer) != 0)
            {
                var type = (uint)Marshal.ReadInt32(buffer);
                switch (type)
                {
                    case Sdl.QuitEvent:
                        input.RequestQuit();
                        break;
                    case Sdl.KeyDownEvent:
                    {
                        var scancode = Marshal.ReadInt32(buffer, 16);
                        if (scancode is ScancodeEscape or ScancodeQuit)
                        {
                            input.RequestQuit();
                            break;
                        }

                        input.AddKeyDown(scancode);
                        break;
                    }

                    case Sdl.KeyUpEvent:
                        input.AddKeyUp(Marshal.ReadInt32(buffer, 16));
                        break;

                    case Sdl.MouseWheelEvent:
                        // The wheel reads as notches in y, at offset 20 of the event struct.
                        input.AddWheel(Marshal.ReadInt32(buffer, 20));
                        break;

                    case Sdl.MouseButtonDownEvent:
                    {
                        var button = Marshal.ReadByte(buffer, 16);
                        if (button == 1)
                        {
                            input.AddClick(new Vector2(Marshal.ReadInt32(buffer, 20), Marshal.ReadInt32(buffer, 24)));
                        }

                        break;
                    }

                    case Sdl.MouseMotionEvent:
                        input.SetMouse(new Vector2(Marshal.ReadInt32(buffer, 20), Marshal.ReadInt32(buffer, 24)));
                        break;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        Sdl.SDL_GetMouseState(out var x, out var y);
        input.SetMouse(new Vector2(x, y));
    }

    public void WritePpm(string path)
    {
        var bytes = new byte[Width * Height * 4];
        Gl.glReadBuffer(Gl.Back);
        unsafe
        {
            fixed (byte* pointer = bytes)
            {
                Gl.glReadPixels(0, 0, Width, Height, Gl.Rgba, Gl.UnsignedByte, (IntPtr)pointer);
            }
        }

        using var stream = File.Create(path);
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write($"P6\n{Width} {Height}\n255\n");
        writer.Flush();
        var row = new byte[Width * 3];
        for (var y = Height - 1; y >= 0; y--)
        {
            for (var x = 0; x < Width; x++)
            {
                var source = ((y * Width) + x) * 4;
                row[x * 3] = bytes[source];
                row[(x * 3) + 1] = bytes[source + 1];
                row[(x * 3) + 2] = bytes[source + 2];
            }

            stream.Write(row);
        }
    }

    public void Dispose()
    {
        _bloom.Dispose();
        Sdl.SDL_GL_DeleteContext(_context);
        Sdl.SDL_DestroyWindow(_window);
        Sdl.SDL_Quit();
    }

    private void FlushShapes(Camera camera)
    {
        if (_shapes.Count > 0)
        {
            Gl.glBindBuffer(Gl.ArrayBuffer, _shapeBuffer);
            unsafe
            {
                fixed (float* pointer = _shapes.Vertices)
                {
                    Gl.glBufferSubData(Gl.ArrayBuffer, IntPtr.Zero, (IntPtr)(_shapes.Count * 4L), (IntPtr)pointer);
                }
            }

            Gl.glBindVertexArray(_shapeVao);
            Gl.glUseProgram(_shapeProgram);
            Gl.glUniform2f(_shapeViewport, Width, Height);
            Gl.glUniform3f(_shapeCamera, camera.Center.X, camera.Center.Y, camera.Zoom);
            Gl.glDrawArrays(Gl.Triangles, 0, _shapes.VertexCount);
        }

        _shapes.Reset();
    }
}
