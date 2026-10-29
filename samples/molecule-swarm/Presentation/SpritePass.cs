using System.Numerics;

namespace MoleculeSwarm;

/// <summary>
/// The point-sprite passes: the horde, the ground stains, and the additive blast glows. A sprite is
/// one vertex, so a million orcs would be a million points, and the shapes live in the shaders.
/// </summary>
internal sealed class SpritePass
{
    private const int MaxGlowPoints = 256;

    private static readonly Vector4 Plasma = new(1f, 0.46f, 0.74f, 0.055f);
    private static readonly Vector4 Ionized = new(0.62f, 0.42f, 1f, 0.03f);

    private readonly uint _orcProgram;
    private readonly int _orcViewport;
    private readonly int _orcCamera;
    private readonly int _orcTime;
    private readonly uint _orcPositions;
    private readonly uint _orcKinds;
    private readonly uint _orcSeeds;
    private readonly uint _orcVao;

    private readonly uint _splatProgram;
    private readonly int _splatViewport;
    private readonly int _splatCamera;
    private readonly int _splatColor;
    private readonly uint _bloodBuffer;
    private readonly uint _bloodVao;
    private readonly uint _scorchBuffer;
    private readonly uint _scorchVao;
    private int _uploadedBlood;
    private int _uploadedScorch;

    private readonly uint _glowProgram;
    private readonly int _glowViewport;
    private readonly int _glowCamera;
    private readonly uint _glowBuffer;
    private readonly uint _glowVao;
    private readonly float[] _glowVertices = new float[MaxGlowPoints * 4];
    private int _glowPoints;

    /// <summary>The view every sprite pass is drawn through; the renderer sets it once a frame.</summary>
    public Camera Camera { get; set; } = Camera.Default;

    public SpritePass()
    {
        _orcProgram = Gl.BuildProgram(Shaders.OrcVertex, Shaders.OrcFragment);
        _orcViewport = Gl.glGetUniformLocation(_orcProgram, "uViewport");
        _orcCamera = Gl.glGetUniformLocation(_orcProgram, "uCamera");
        _orcTime = Gl.glGetUniformLocation(_orcProgram, "uTime");

        var buffers = new uint[6];
        Gl.glGenBuffers(6, buffers);
        _orcPositions = buffers[0];
        _orcKinds = buffers[1];
        _orcSeeds = buffers[2];
        _bloodBuffer = buffers[3];
        _scorchBuffer = buffers[4];
        _glowBuffer = buffers[5];

        var arrays = new uint[4];
        Gl.glGenVertexArrays(4, arrays);
        _orcVao = arrays[0];
        _bloodVao = arrays[1];
        _scorchVao = arrays[2];
        _glowVao = arrays[3];

        Gl.glBindVertexArray(_orcVao);
        Gl.glBindBuffer(Gl.ArrayBuffer, _orcPositions);
        Gl.glBufferData(Gl.ArrayBuffer, (IntPtr)(Sim.MaxMolecules * 8L), IntPtr.Zero, Gl.DynamicDraw);
        Gl.glEnableVertexAttribArray(0);
        Gl.glVertexAttribPointer(0, 2, Gl.Float, false, 8, IntPtr.Zero);
        Gl.glBindBuffer(Gl.ArrayBuffer, _orcKinds);
        Gl.glBufferData(Gl.ArrayBuffer, (IntPtr)(Sim.MaxMolecules * 4L), IntPtr.Zero, Gl.DynamicDraw);
        Gl.glEnableVertexAttribArray(1);
        Gl.glVertexAttribPointer(1, 1, Gl.Float, false, 4, IntPtr.Zero);
        Gl.glBindBuffer(Gl.ArrayBuffer, _orcSeeds);
        Gl.glBufferData(Gl.ArrayBuffer, (IntPtr)(Sim.MaxMolecules * 4L), IntPtr.Zero, Gl.DynamicDraw);
        Gl.glEnableVertexAttribArray(2);
        Gl.glVertexAttribPointer(2, 1, Gl.Float, false, 4, IntPtr.Zero);

        SetupSplatVao(_bloodVao, _bloodBuffer, Effects.MaxBlood);
        SetupSplatVao(_scorchVao, _scorchBuffer, Effects.MaxScorch);

        _splatProgram = Gl.BuildProgram(Shaders.SplatVertex, Shaders.SplatFragment);
        _splatViewport = Gl.glGetUniformLocation(_splatProgram, "uViewport");
        _splatCamera = Gl.glGetUniformLocation(_splatProgram, "uCamera");
        _splatColor = Gl.glGetUniformLocation(_splatProgram, "uColor");

        Gl.glBindVertexArray(_glowVao);
        Gl.glBindBuffer(Gl.ArrayBuffer, _glowBuffer);
        Gl.glBufferData(Gl.ArrayBuffer, (IntPtr)(MaxGlowPoints * 16L), IntPtr.Zero, Gl.DynamicDraw);
        Gl.glEnableVertexAttribArray(0);
        Gl.glVertexAttribPointer(0, 2, Gl.Float, false, 16, IntPtr.Zero);
        Gl.glEnableVertexAttribArray(1);
        Gl.glVertexAttribPointer(1, 1, Gl.Float, false, 16, (IntPtr)8);
        Gl.glEnableVertexAttribArray(2);
        Gl.glVertexAttribPointer(2, 1, Gl.Float, false, 16, (IntPtr)12);

        _glowProgram = Gl.BuildProgram(Shaders.GlowVertex, Shaders.GlowFragment);
        _glowViewport = Gl.glGetUniformLocation(_glowProgram, "uViewport");
        _glowCamera = Gl.glGetUniformLocation(_glowProgram, "uCamera");
    }

    public void DrawOrcs(Swarm horde, float time)
    {
        var count = horde.DrawPositions.Length;
        if (count == 0)
        {
            return;
        }

        Upload(horde.DrawPositions, _orcPositions, 8);
        Upload(horde.DrawKinds, _orcKinds, 4);
        Upload(horde.DrawSeeds, _orcSeeds, 4);

        Gl.glBindVertexArray(_orcVao);
        Gl.glUseProgram(_orcProgram);
        Gl.glUniform2f(_orcViewport, Sim.Width, Sim.Height);
        Gl.glUniform3f(_orcCamera, Camera.Center.X, Camera.Center.Y, Camera.Zoom);
        Gl.glUniform1f(_orcTime, time);
        Gl.glEnable(Gl.ProgramPointSize);
        Gl.glBlendFunc(Gl.SrcAlpha, Gl.One);
        Gl.glDrawArrays(Gl.Points, 0, count);
        Gl.glBlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
    }

    public void DrawSplats(Effects effects)
    {
        if (effects.BloodCount > _uploadedBlood)
        {
            UploadSplats(effects.Blood, _bloodBuffer, _uploadedBlood, effects.BloodCount);
            _uploadedBlood = effects.BloodCount;
        }

        if (effects.ScorchCount > _uploadedScorch)
        {
            UploadSplats(effects.Scorch, _scorchBuffer, _uploadedScorch, effects.ScorchCount);
            _uploadedScorch = effects.ScorchCount;
        }

        Gl.glUseProgram(_splatProgram);
        Gl.glUniform2f(_splatViewport, Sim.Width, Sim.Height);
        Gl.glUniform3f(_splatCamera, Camera.Center.X, Camera.Center.Y, Camera.Zoom);

        if (effects.BloodCount > 0)
        {
            Gl.glBlendFunc(Gl.SrcAlpha, Gl.One);
            Gl.glUniform4f(_splatColor, Plasma.X, Plasma.Y, Plasma.Z, Plasma.W);
            Gl.glBindVertexArray(_bloodVao);
            Gl.glDrawArrays(Gl.Points, 0, effects.BloodCount);
            Gl.glBlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
        }

        if (effects.ScorchCount > 0)
        {
            // The golden blast residue glows, so it adds rather than paints over the dirt.
            Gl.glBlendFunc(Gl.SrcAlpha, Gl.One);
            Gl.glUniform4f(_splatColor, Ionized.X, Ionized.Y, Ionized.Z, Ionized.W);
            Gl.glBindVertexArray(_scorchVao);
            Gl.glDrawArrays(Gl.Points, 0, effects.ScorchCount);
            Gl.glBlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
        }
    }

    public void DrawGlows(Effects effects)
    {
        _glowPoints = 0;
        for (var index = 0; index < effects.BoomCount; index++)
        {
            var boom = effects.BoomAt(index);
            var life = Math.Clamp(boom.Life / boom.MaxLife, 0f, 1f);
            var radius = boom.Radius * (0.45f + (0.75f * (1f - life)));
            AddGlow(boom.Center, radius, life * 0.85f);
        }

        if (_glowPoints == 0)
        {
            return;
        }

        Gl.glBindBuffer(Gl.ArrayBuffer, _glowBuffer);
        unsafe
        {
            fixed (float* pointer = _glowVertices)
            {
                Gl.glBufferSubData(Gl.ArrayBuffer, IntPtr.Zero, (IntPtr)(_glowPoints * 4L * 4L), (IntPtr)pointer);
            }
        }

        Gl.glBlendFunc(Gl.SrcAlpha, Gl.One);
        Gl.glBindVertexArray(_glowVao);
        Gl.glUseProgram(_glowProgram);
        Gl.glUniform2f(_glowViewport, Sim.Width, Sim.Height);
        Gl.glUniform3f(_glowCamera, Camera.Center.X, Camera.Center.Y, Camera.Zoom);
        Gl.glDrawArrays(Gl.Points, 0, _glowPoints);
        Gl.glBlendFunc(Gl.SrcAlpha, Gl.OneMinusSrcAlpha);
    }

    private void AddGlow(Vector2 center, float radius, float alpha)
    {
        if (_glowPoints >= MaxGlowPoints)
        {
            return;
        }

        var offset = _glowPoints * 4;
        _glowVertices[offset] = center.X;
        _glowVertices[offset + 1] = center.Y;
        _glowVertices[offset + 2] = radius;
        _glowVertices[offset + 3] = alpha;
        _glowPoints++;
    }

    private static void SetupSplatVao(uint vao, uint buffer, int capacity)
    {
        Gl.glBindVertexArray(vao);
        Gl.glBindBuffer(Gl.ArrayBuffer, buffer);
        Gl.glBufferData(Gl.ArrayBuffer, (IntPtr)(capacity * 16L), IntPtr.Zero, Gl.DynamicDraw);
        Gl.glEnableVertexAttribArray(0);
        Gl.glVertexAttribPointer(0, 2, Gl.Float, false, 16, IntPtr.Zero);
        Gl.glEnableVertexAttribArray(1);
        Gl.glVertexAttribPointer(1, 1, Gl.Float, false, 16, (IntPtr)8);
        Gl.glEnableVertexAttribArray(2);
        Gl.glVertexAttribPointer(2, 1, Gl.Float, false, 16, (IntPtr)12);
    }

    private static unsafe void Upload<T>(Span<T> data, uint buffer, int elementSize)
        where T : unmanaged
    {
        if (data.Length == 0)
        {
            return;
        }

        Gl.glBindBuffer(Gl.ArrayBuffer, buffer);
        fixed (T* pointer = data)
        {
            Gl.glBufferSubData(Gl.ArrayBuffer, IntPtr.Zero, (IntPtr)(data.Length * (long)elementSize),
                (IntPtr)pointer);
        }
    }

    private static unsafe void UploadSplats(Splat[] splats, uint buffer, int start, int count)
    {
        Gl.glBindBuffer(Gl.ArrayBuffer, buffer);
        fixed (Splat* pointer = splats)
        {
            Gl.glBufferSubData(
                Gl.ArrayBuffer,
                (IntPtr)(start * 16L),
                (IntPtr)((count - start) * 16L),
                (IntPtr)(pointer + start));
        }
    }
}
