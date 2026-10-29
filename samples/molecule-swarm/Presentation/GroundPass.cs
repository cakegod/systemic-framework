namespace MoleculeSwarm;

/// <summary>Draws the terrain: one baked RGBA image stretched over a full-screen quad, with vignette.</summary>
internal sealed class GroundPass
{
    private readonly uint _program;
    private readonly uint _buffer;
    private readonly uint _vao;
    private readonly uint[] _textureName = new uint[1];
    private readonly int _imageLocation;
    private readonly int _viewportLocation;
    private readonly int _cameraLocation;
    private uint _texture;
    private Terrain? _uploadedTerrain;

    public GroundPass()
    {
        _program = Gl.BuildProgram(Shaders.TerrainVertex, Shaders.TerrainFragment);
        _imageLocation = Gl.glGetUniformLocation(_program, "uImage");
        _viewportLocation = Gl.glGetUniformLocation(_program, "uViewport");
        _cameraLocation = Gl.glGetUniformLocation(_program, "uCamera");

        var buffers = new uint[1];
        Gl.glGenBuffers(1, buffers);
        _buffer = buffers[0];

        var arrays = new uint[1];
        Gl.glGenVertexArrays(1, arrays);
        _vao = arrays[0];

        // One full-screen quad in clip space with the image's top row at uv.v = 0.
        float[] quad =
        [
            -1f, -1f, 0f, 1f, 1f, -1f, 1f, 1f, 1f, 1f, 1f, 0f,
            -1f, -1f, 0f, 1f, 1f, 1f, 1f, 0f, -1f, 1f, 0f, 0f,
        ];
        Gl.glBindVertexArray(_vao);
        Gl.glBindBuffer(Gl.ArrayBuffer, _buffer);
        unsafe
        {
            fixed (float* data = quad)
            {
                Gl.glBufferData(Gl.ArrayBuffer, (IntPtr)(quad.Length * 4L), (IntPtr)data, Gl.StaticDraw);
            }
        }

        Gl.glEnableVertexAttribArray(0);
        Gl.glVertexAttribPointer(0, 2, Gl.Float, false, 16, IntPtr.Zero);
        Gl.glEnableVertexAttribArray(1);
        Gl.glVertexAttribPointer(1, 2, Gl.Float, false, 16, (IntPtr)8);
    }

    public void Draw(Terrain terrain, Camera camera)
    {
        // WHY: restart builds a new Sim, so the arena image changes while this renderer stays alive.
        if (_texture == 0 || !ReferenceEquals(_uploadedTerrain, terrain))
        {
            _uploadedTerrain = terrain;
            var image = terrain.Image;
            if (_texture == 0)
            {
                Gl.glGenTextures(1, _textureName);
                _texture = _textureName[0];
            }

            Gl.glBindTexture(Gl.Texture2D, _texture);
            Gl.glTexParameteri(Gl.Texture2D, Gl.TextureMinFilter, Gl.Nearest);
            Gl.glTexParameteri(Gl.Texture2D, Gl.TextureMagFilter, Gl.Nearest);
            Gl.glTexParameteri(Gl.Texture2D, Gl.TextureWrapS, Gl.ClampToEdge);
            Gl.glTexParameteri(Gl.Texture2D, Gl.TextureWrapT, Gl.ClampToEdge);
            unsafe
            {
                fixed (byte* pixels = image)
                {
                    Gl.glTexImage2D(Gl.Texture2D, 0, (int)Gl.Rgba8, Sim.Width, Sim.Height, 0, Gl.Rgba,
                        Gl.UnsignedByte, (IntPtr)pixels);
                }
            }
        }

        Gl.glDisable(Gl.Blend);
        Gl.glUseProgram(_program);
        Gl.glUniform2f(_viewportLocation, Sim.Width, Sim.Height);
        Gl.glUniform3f(_cameraLocation, camera.Center.X, camera.Center.Y, camera.Zoom);
        Gl.glActiveTexture(Gl.Texture0);
        Gl.glBindTexture(Gl.Texture2D, _texture);
        Gl.glUniform1i(_imageLocation, 0);
        Gl.glBindVertexArray(_vao);
        Gl.glDrawArrays(Gl.Triangles, 0, 6);
        Gl.glEnable(Gl.Blend);
        Gl.glBlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
    }
}
