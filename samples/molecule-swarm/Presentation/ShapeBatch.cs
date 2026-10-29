using System.Globalization;
using System.Numerics;

namespace MoleculeSwarm;

/// <summary>
/// One growable batch of coloured triangles: quads, discs, rings, arcs, bars and bitmap text all
/// land in the same float buffer, which the renderer uploads in a single draw.
/// </summary>
internal sealed class ShapeBatch
{
    public const int MaxVertices = 6 * 3 * 60_000;

    private readonly float[] _vertices = new float[MaxVertices];
    private int _count;

    /// <summary>Floats written so far: six per vertex, two for position and four for colour.</summary>
    public int Count => _count;

    public Span<float> Vertices => _vertices.AsSpan(0, _count);

    public int VertexCount => _count / 6;

    public void Reset() => _count = 0;

    public void AddQuad(float left, float top, float right, float bottom, Vector4 color)
    {
        Vertex(left, top, color);
        Vertex(right, top, color);
        Vertex(right, bottom, color);
        Vertex(left, top, color);
        Vertex(right, bottom, color);
        Vertex(left, bottom, color);
    }

    public void AddCircle(Vector2 center, float radius, int segments, Vector4 color)
    {
        for (var segment = 0; segment < segments; segment++)
        {
            var a = (MathF.Tau * segment) / segments;
            var b = (MathF.Tau * (segment + 1)) / segments;
            Vertex(center.X, center.Y, color);
            Vertex(center.X + (MathF.Cos(a) * radius), center.Y + (MathF.Sin(a) * radius), color);
            Vertex(center.X + (MathF.Cos(b) * radius), center.Y + (MathF.Sin(b) * radius), color);
        }
    }

    public void AddRing(Vector2 center, float radius, float thickness, int segments, Vector4 color)
    {
        for (var segment = 0; segment < segments; segment++)
        {
            var a = (MathF.Tau * segment) / segments;
            var b = (MathF.Tau * (segment + 1)) / segments;
            var inner = radius - thickness;
            var outer = radius + thickness;
            Vertex(center.X + (MathF.Cos(a) * inner), center.Y + (MathF.Sin(a) * inner), color);
            Vertex(center.X + (MathF.Cos(a) * outer), center.Y + (MathF.Sin(a) * outer), color);
            Vertex(center.X + (MathF.Cos(b) * outer), center.Y + (MathF.Sin(b) * outer), color);
            Vertex(center.X + (MathF.Cos(a) * inner), center.Y + (MathF.Sin(a) * inner), color);
            Vertex(center.X + (MathF.Cos(b) * outer), center.Y + (MathF.Sin(b) * outer), color);
            Vertex(center.X + (MathF.Cos(b) * inner), center.Y + (MathF.Sin(b) * inner), color);
        }
    }

    public void AddArc(Vector2 center, float radius, float thickness, float step, int segments,
        float start, float sweep, Vector4 color)
    {
        var angle = start;
        var end = start + sweep;
        for (var segment = 0; segment < segments && angle < end; segment++)
        {
            var next = MathF.Min(angle + step, end);
            var inner = radius - (thickness * 0.5f);
            var outer = radius + (thickness * 0.5f);
            var cosA = MathF.Cos(angle);
            var sinA = MathF.Sin(angle);
            var cosB = MathF.Cos(next);
            var sinB = MathF.Sin(next);
            Vertex(center.X + (cosA * inner), center.Y + (sinA * inner), color);
            Vertex(center.X + (cosA * outer), center.Y + (sinA * outer), color);
            Vertex(center.X + (cosB * outer), center.Y + (sinB * outer), color);
            Vertex(center.X + (cosA * inner), center.Y + (sinA * inner), color);
            Vertex(center.X + (cosB * outer), center.Y + (sinB * outer), color);
            Vertex(center.X + (cosB * inner), center.Y + (sinB * inner), color);
            angle = next;
        }
    }

    public void AddThickLine(Vector2 from, Vector2 to, float width, Vector4 color)
    {
        var delta = to - from;
        var length = delta.Length();
        if (length < 0.001f)
        {
            return;
        }

        var normal = new Vector2(-delta.Y / length, delta.X / length) * (width * 0.5f);
        Vertex(from.X + normal.X, from.Y + normal.Y, color);
        Vertex(to.X + normal.X, to.Y + normal.Y, color);
        Vertex(to.X - normal.X, to.Y - normal.Y, color);
        Vertex(from.X + normal.X, from.Y + normal.Y, color);
        Vertex(to.X - normal.X, to.Y - normal.Y, color);
        Vertex(from.X - normal.X, from.Y - normal.Y, color);
    }

    /// <summary>A rectangular bar pointing along a direction, used for turret housings.</summary>
    public void AddBar(Vector2 center, Vector2 direction, float forward, float back, float halfWidth,
        Vector4 color)
    {
        var side = new Vector2(-direction.Y, direction.X) * halfWidth;
        var nose = center + (direction * forward);
        var tail = center - (direction * back);
        Vertex(nose.X + side.X, nose.Y + side.Y, color);
        Vertex(nose.X - side.X, nose.Y - side.Y, color);
        Vertex(tail.X - side.X, tail.Y - side.Y, color);
        Vertex(nose.X + side.X, nose.Y + side.Y, color);
        Vertex(tail.X - side.X, tail.Y - side.Y, color);
        Vertex(tail.X + side.X, tail.Y + side.Y, color);
    }

    public void AddText(string text, float x, float y, int scale, Vector4 color)
    {
        var cursor = x;
        foreach (var character in text)
        {
            if (character == ' ')
            {
                cursor += 4 * scale;
                continue;
            }

            if (!Glyphs.TryGet(character, out var rows))
            {
                cursor += 6 * scale;
                continue;
            }

            for (var row = 0; row < 7; row++)
            {
                var bits = rows[row];
                for (var column = 0; column < 5; column++)
                {
                    if ((bits & (1 << (4 - column))) == 0)
                    {
                        continue;
                    }

                    AddQuad(cursor + (column * scale), y + (row * scale),
                        cursor + ((column + 1) * scale), y + ((row + 1) * scale), color);
                }
            }

            cursor += 6 * scale;
        }
    }

    public void AddTextShadow(string text, float x, float y, int scale, Vector4 color)
    {
        var shadow = new Vector4(0f, 0f, 0f, color.W * 0.85f);
        AddText(text, x + scale, y + scale, scale, shadow);
        AddText(text, x, y, scale, color);
    }

    public void AddTextCentered(string text, float centerX, float y, int scale, Vector4 color)
    {
        var width = (text.Length * 6 * scale) - (2 * scale);
        AddTextShadow(text, centerX - (width * 0.5f), y, scale, color);
    }

    public static string Compact(int value) => value switch
    {
        >= 1_000_000 => (value / 1_000_000f).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 10_000 => (value / 1000).ToString(CultureInfo.InvariantCulture) + "K",
        >= 1_000 => (value / 1000f).ToString("0.#", CultureInfo.InvariantCulture) + "K",
        _ => value.ToString(CultureInfo.InvariantCulture),
    };

    private void Vertex(float x, float y, Vector4 color)
    {
        var offset = _count;
        if (offset + 6 > _vertices.Length)
        {
            return;
        }

        _vertices[offset] = x;
        _vertices[offset + 1] = y;
        _vertices[offset + 2] = color.X;
        _vertices[offset + 3] = color.Y;
        _vertices[offset + 4] = color.Z;
        _vertices[offset + 5] = color.W;
        _count += 6;
    }
}
