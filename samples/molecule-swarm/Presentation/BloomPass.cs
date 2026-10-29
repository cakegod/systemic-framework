namespace MoleculeSwarm;

/// <summary>
/// The bloom: the frame is drawn into a texture, the part of it bright enough to spill light is
/// extracted at a quarter resolution, blurred along each axis, and added back over the scene. The
/// whole point is that the molecules are additive points of light, so their halo accumulates and
/// bleeds over the ground instead of stopping at the sprite's edge.
/// </summary>
internal sealed class BloomPass : IDisposable
{
    /// <summary>Luminance below this does not bloom: the dirt and the pools stay put.</summary>
    private const float Threshold = 0.55f;

    /// <summary>How much of the blurred light is added back. Above one, the glow carries the scene.</summary>
    private const float Strength = 1.1f;

    /// <summary>The glow is built at this fraction of the frame; blurring a quarter frame is cheap.</summary>
    private const int Downscale = 4;

    private readonly int _glowWidth = Renderer.Width / Downscale;
    private readonly int _glowHeight = Renderer.Height / Downscale;

    private readonly uint _quadBuffer;
    private readonly uint _quadVao;

    private readonly uint _brightProgram;
    private readonly int _brightImage;
    private readonly int _brightThreshold;

    private readonly uint _blurProgram;
    private readonly int _blurImage;
    private readonly int _blurDirection;

    private readonly uint _bloomProgram;
    private readonly int _bloomScene;
    private readonly int _bloomGlow;
    private readonly int _bloomStrength;

    private readonly uint[] _framebuffers = new uint[3];
    private readonly uint[] _textures = new uint[3];

    public BloomPass()
    {
        _brightProgram = Gl.BuildProgram(Shaders.QuadVertex, Shaders.BrightFragment);
        _brightImage = Gl.glGetUniformLocation(_brightProgram, "uImage");
        _brightThreshold = Gl.glGetUniformLocation(_brightProgram, "uThreshold");

        _blurProgram = Gl.BuildProgram(Shaders.QuadVertex, Shaders.BlurFragment);
        _blurImage = Gl.glGetUniformLocation(_blurProgram, "uImage");
        _blurDirection = Gl.glGetUniformLocation(_blurProgram, "uDirection");

        _bloomProgram = Gl.BuildProgram(Shaders.QuadVertex, Shaders.BloomFragment);
        _bloomScene = Gl.glGetUniformLocation(_bloomProgram, "uScene");
        _bloomGlow = Gl.glGetUniformLocation(_bloomProgram, "uGlow");
        _bloomStrength = Gl.glGetUniformLocation(_bloomProgram, "uStrength");

        var buffers = new uint[1];
        Gl.glGenBuffers(1, buffers);
        _quadBuffer = buffers[0];

        var arrays = new uint[1];
        Gl.glGenVertexArrays(1, arrays);
        _quadVao = arrays[0];

        float[] quad = [-1f, -1f, 1f, -1f, 1f, 1f, -1f, -1f, 1f, 1f, -1f, 1f];
        Gl.glBindVertexArray(_quadVao);
        Gl.glBindBuffer(Gl.ArrayBuffer, _quadBuffer);
        unsafe
        {
            fixed (float* data = quad)
            {
                Gl.glBufferData(Gl.ArrayBuffer, (IntPtr)(quad.Length * 4L), (IntPtr)data, Gl.StaticDraw);
            }
        }

        Gl.glEnableVertexAttribArray(0);
        Gl.glVertexAttribPointer(0, 2, Gl.Float, false, 8, IntPtr.Zero);

        Gl.glGenTextures(_textures.Length, _textures);
        Gl.glGenFramebuffers(_framebuffers.Length, _framebuffers);
        Attach(_framebuffers[0], _textures[0], Renderer.Width, Renderer.Height);
        Attach(_framebuffers[1], _textures[1], _glowWidth, _glowHeight);
        Attach(_framebuffers[2], _textures[2], _glowWidth, _glowHeight);
        Gl.glBindFramebuffer(Gl.Framebuffer, 0);
    }

    private static void Attach(uint framebuffer, uint texture, int width, int height)
    {
        Gl.glBindTexture(Gl.Texture2D, texture);
        Gl.glTexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.Linear);
        Gl.glTexParameteri(Gl.Texture2D, Gl.TextureMagFilter, Gl.Linear);
        Gl.glTexParameteri(Gl.Texture2D, Gl.TextureWrapS, Gl.ClampToEdge);
        Gl.glTexParameteri(Gl.Texture2D, Gl.TextureWrapT, Gl.ClampToEdge);
        Gl.glTexImage2D(Gl.Texture2D, 0, (int)Gl.Rgba8, width, height, 0, Gl.Rgba, Gl.UnsignedByte, IntPtr.Zero);

        Gl.glBindFramebuffer(Gl.Framebuffer, framebuffer);
        Gl.glFramebufferTexture2D(Gl.Framebuffer, Gl.ColorAttachment0, Gl.Texture2D, texture, 0);
        if (Gl.glCheckFramebufferStatus(Gl.Framebuffer) != Gl.FramebufferComplete)
        {
            throw new InvalidOperationException($"Bloom framebuffer {width}x{height} is incomplete.");
        }
    }

    /// <summary>Points the drawing at the scene texture; everything drawn until <see cref="Resolve"/> lands there.</summary>
    public void Begin()
    {
        Gl.glBindFramebuffer(Gl.Framebuffer, _framebuffers[0]);
        Gl.glViewport(0, 0, Renderer.Width, Renderer.Height);
    }

    /// <summary>Draws one texture into another at quarter resolution, blurred along one axis.</summary>
    private void Blur(int source, int target, float x, float y)
    {
        Gl.glBindFramebuffer(Gl.Framebuffer, _framebuffers[target]);
        Gl.glBindTexture(Gl.Texture2D, _textures[source]);
        Gl.glUniform2f(_blurDirection, x, y);
        Gl.glDrawArrays(Gl.Triangles, 0, 6);
    }

    /// <summary>Adds the blurred brightness of the scene over it, on the window's framebuffer.</summary>
    public void Resolve()
    {
        Gl.glDisable(Gl.Blend);
        Gl.glBindVertexArray(_quadVao);

        // Bright pass into the first quarter-res texture.
        Gl.glBindFramebuffer(Gl.Framebuffer, _framebuffers[1]);
        Gl.glViewport(0, 0, _glowWidth, _glowHeight);
        Gl.glUseProgram(_brightProgram);
        Gl.glActiveTexture(Gl.Texture0);
        Gl.glBindTexture(Gl.Texture2D, _textures[0]);
        Gl.glUniform1i(_brightImage, 0);
        Gl.glUniform1f(_brightThreshold, Threshold);
        Gl.glDrawArrays(Gl.Triangles, 0, 6);

        // Two separable blurs, ping-ponging between the two glow textures. Each pair of passes
        // widens the smear, so a bright point spills over a good part of the frame.
        Gl.glUseProgram(_blurProgram);
        Gl.glUniform1i(_blurImage, 0);
        for (var pass = 0; pass < 3; pass++)
        {
            Blur(1, 2, 1f / _glowWidth, 0f);
            Blur(2, 1, 0f, 1f / _glowHeight);
        }

        // Composite: the scene texture, plus the blurred light.
        Gl.glBindFramebuffer(Gl.Framebuffer, 0);
        Gl.glViewport(0, 0, Renderer.Width, Renderer.Height);
        Gl.glUseProgram(_bloomProgram);
        Gl.glActiveTexture(Gl.Texture0);
        Gl.glBindTexture(Gl.Texture2D, _textures[0]);
        Gl.glUniform1i(_bloomScene, 0);
        Gl.glActiveTexture(Gl.Texture0 + 1);
        Gl.glBindTexture(Gl.Texture2D, _textures[1]);
        Gl.glUniform1i(_bloomGlow, 1);
        Gl.glUniform1f(_bloomStrength, Strength);
        Gl.glDrawArrays(Gl.Triangles, 0, 6);
        Gl.glActiveTexture(Gl.Texture0);

        Gl.glEnable(Gl.Blend);
        Gl.glBlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
    }

    public void Dispose()
    {
        Gl.glDeleteFramebuffers(_framebuffers.Length, _framebuffers);
    }
}
