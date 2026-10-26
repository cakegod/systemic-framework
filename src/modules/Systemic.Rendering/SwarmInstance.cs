using System.Numerics;
using System.Runtime.InteropServices;

namespace Systemic.Rendering;

/// <summary>One drawn object of a swarm. Fixed layout so every host and game shares one buffer format.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct SwarmInstance(Vector2 position, float size, float rotation, uint rgba)
{
    public Vector2 Position { get; } = position;

    public float Size { get; } = size;

    public float Rotation { get; } = rotation;

    /// <summary>Packed RGBA in 0xAABBGGRR order, as returned by <see cref="SwarmColor.Pack"/>.</summary>
    public uint Rgba { get; } = rgba;
}
