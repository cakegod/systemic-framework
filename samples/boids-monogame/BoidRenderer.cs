using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Systemic.Rendering;
using XnaColor = Microsoft.Xna.Framework.Color;
using NumericsVector2 = System.Numerics.Vector2;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;

namespace Boids.MonoGame;

public sealed class BoidRenderer : IDisposable
{
    private readonly GraphicsDevice _graphicsDevice;
    private readonly BasicEffect _effect;
    private readonly Texture2D _particleTexture;
    private readonly SwarmScene _scene;
    private readonly SwarmSource[] _sources;
    private readonly Memory<SwarmInstance>[] _windows;
    private readonly int _population;
    private readonly int[] _speciesCounts;
    private readonly uint[] _packedColors;
    private readonly int[] _slots;
    private readonly VertexPositionColorTexture[] _vertices;
    private readonly DynamicVertexBuffer _vertexBuffer;
    private readonly IndexBuffer _indexBuffer;
    private readonly XnaVector2 _textureOrigin;
    private readonly float _particleRadius;
    private readonly float _starvedSpeedFactor;
    private int _lastWrittenTick = int.MinValue;
    private bool _disposed;

    public BoidRenderer(
        GraphicsDevice graphicsDevice,
        Texture2D particleTexture,
        SwarmScene scene,
        int population,
        float particleRadius,
        float starvedSpeedFactor,
        ReadOnlySpan<ParticleSpeciesView> species,
        ReadOnlySpan<int> speciesByBoid)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        ArgumentNullException.ThrowIfNull(particleTexture);
        ArgumentNullException.ThrowIfNull(scene);
        if (population < 0 || species.Length == 0 || speciesByBoid.Length != population)
        {
            throw new ArgumentException("Renderer species data must cover every particle.");
        }

        _graphicsDevice = graphicsDevice;
        _particleTexture = particleTexture;
        _scene = scene;
        _population = population;
        _particleRadius = particleRadius;
        _starvedSpeedFactor = starvedSpeedFactor;
        _textureOrigin = new XnaVector2(particleTexture.Width * 0.5f, particleTexture.Height * 0.5f);
        _speciesCounts = new int[species.Length];
        _packedColors = new uint[species.Length];
        _sources = new SwarmSource[species.Length];
        _windows = new Memory<SwarmInstance>[species.Length];
        _slots = new int[species.Length];
        _vertices = new VertexPositionColorTexture[checked(population * 4)];

        for (var index = 0; index < species.Length; index++)
        {
            var definition = species[index];
            if (definition.Index != index)
            {
                throw new ArgumentException("Renderer species indices must be contiguous from zero.", nameof(species));
            }

            _packedColors[index] = SwarmColor.Pack(
                (byte)definition.Red, (byte)definition.Green, (byte)definition.Blue);
        }

        foreach (var speciesIndex in speciesByBoid)
        {
            if ((uint)speciesIndex >= (uint)_speciesCounts.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(speciesByBoid));
            }

            _speciesCounts[speciesIndex]++;
        }

        for (var index = 0; index < species.Length; index++)
        {
            var source = new SwarmSource($"boids.species.{index}", _speciesCounts[index]);
            _sources[index] = source;
            scene.Add(source);
        }

        var indices = new int[checked(population * 6)];
        for (var boid = 0; boid < population; boid++)
        {
            var vertex = boid * 4;
            var index = boid * 6;
            indices[index] = vertex;
            indices[index + 1] = vertex + 1;
            indices[index + 2] = vertex + 2;
            indices[index + 3] = vertex + 2;
            indices[index + 4] = vertex + 1;
            indices[index + 5] = vertex + 3;
        }

        _vertexBuffer = new DynamicVertexBuffer(
            graphicsDevice,
            VertexPositionColorTexture.VertexDeclaration,
            Math.Max(1, _vertices.Length),
            BufferUsage.WriteOnly);
        _indexBuffer = new IndexBuffer(
            graphicsDevice,
            IndexElementSize.ThirtyTwoBits,
            indices.Length,
            BufferUsage.WriteOnly);
        _indexBuffer.SetData(indices);

        _effect = new BasicEffect(graphicsDevice)
        {
            LightingEnabled = false,
            TextureEnabled = true,
            Texture = particleTexture,
            VertexColorEnabled = true,
            World = Matrix.Identity,
            View = Matrix.Identity,
        };
    }

    public void Draw(
        ReadOnlySpan<NumericsVector2> positions,
        ReadOnlySpan<float> energies,
        ReadOnlySpan<int> speciesByBoid,
        int simulationTick,
        Matrix projection)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (positions.Length != _population || energies.Length != _population
            || speciesByBoid.Length != _population)
        {
            throw new ArgumentException("Boid state does not match the renderer's instance count.");
        }

        if (simulationTick != _lastWrittenTick)
        {
            WriteInstances(positions, energies, speciesByBoid);
            _vertexBuffer.SetData(_vertices, 0, _vertices.Length, SetDataOptions.Discard);
            _lastWrittenTick = simulationTick;
        }

        _effect.Projection = projection;
        _graphicsDevice.BlendState = BlendState.AlphaBlend;
        _graphicsDevice.DepthStencilState = DepthStencilState.None;
        _graphicsDevice.RasterizerState = RasterizerState.CullNone;
        _graphicsDevice.SamplerStates[0] = SamplerState.LinearClamp;
        _graphicsDevice.SetVertexBuffer(_vertexBuffer);
        _graphicsDevice.Indices = _indexBuffer;

        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            _graphicsDevice.DrawIndexedPrimitives(
                PrimitiveType.TriangleList, 0, 0, _population * 2);
        }
    }

    private void WriteInstances(ReadOnlySpan<NumericsVector2> positions, ReadOnlySpan<float> energies,
        ReadOnlySpan<int> speciesByBoid)
    {
        Array.Clear(_slots);
        for (var index = 0; index < _sources.Length; index++)
        {
            _windows[index] = _sources[index].BeginWrite(_speciesCounts[index]);
        }

        for (var boid = 0; boid < positions.Length; boid++)
        {
            var species = speciesByBoid[boid];
            var size = _particleRadius
                * BoidFlockSystem.EnergySpeedFactor(energies[boid], _starvedSpeedFactor);
            var position = positions[boid];
            _windows[species].Span[_slots[species]++] = new SwarmInstance(
                new System.Numerics.Vector2(position.X, position.Y),
                size,
                0.0f,
                _packedColors[species]);
        }

        foreach (var source in _sources)
        {
            source.EndWrite();
        }

        var vertexIndex = 0;
        foreach (var source in _sources)
        {
            foreach (var instance in source.Instances)
            {
                var (red, green, blue, alpha) = SwarmColor.Unpack(instance.Rgba);
                var color = new XnaColor(red, green, blue, alpha);
                var center = new XnaVector2(instance.Position.X, instance.Position.Y);
                var radius = instance.Size;
                var rotation = instance.Rotation;
                var sine = rotation == 0.0f ? 0.0f : MathF.Sin(rotation);
                var cosine = rotation == 0.0f ? 1.0f : MathF.Cos(rotation);
                _vertices[vertexIndex++] = CreateVertex(center, new XnaVector2(-radius, -radius), color,
                    new XnaVector2(0, 0), sine, cosine);
                _vertices[vertexIndex++] = CreateVertex(center, new XnaVector2(radius, -radius), color,
                    new XnaVector2(1, 0), sine, cosine);
                _vertices[vertexIndex++] = CreateVertex(center, new XnaVector2(-radius, radius), color,
                    new XnaVector2(0, 1), sine, cosine);
                _vertices[vertexIndex++] = CreateVertex(center, new XnaVector2(radius, radius), color,
                    new XnaVector2(1, 1), sine, cosine);
            }
        }

        if (vertexIndex != _vertices.Length)
        {
            throw new InvalidOperationException($"Expected {_vertices.Length} boid vertices, wrote {vertexIndex}.");
        }
    }

    private static VertexPositionColorTexture CreateVertex(
        XnaVector2 center,
        XnaVector2 offset,
        XnaColor color,
        XnaVector2 textureCoordinate,
        float sine,
        float cosine)
    {
        if (sine != 0.0f)
        {
            offset = new XnaVector2(
                offset.X * cosine - offset.Y * sine,
                offset.X * sine + offset.Y * cosine);
        }

        return new VertexPositionColorTexture(new XnaVector3(center + offset, 0.0f), color, textureCoordinate);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _effect.Dispose();
        _vertexBuffer.Dispose();
        _indexBuffer.Dispose();
        foreach (var source in _sources)
        {
            _scene.Remove(source);
        }

        _disposed = true;
    }
}
