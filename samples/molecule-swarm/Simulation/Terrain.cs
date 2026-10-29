using System.Numerics;

namespace MoleculeSwarm;

/// <summary>
/// The arena: a dark mass filling most of the field, crossed by glowing plasma lanes that run from the
/// border to the core. The lanes are drawn from the run's seed and their width is solved so the mass
/// covers 75-90% of the field while every lane still reaches the core, which is what `Distance`
/// answers for the flow field, the crowd and the baked ground image alike.
/// </summary>
internal sealed class Terrain
{
    public const int Cell = 16;
    public const int Columns = Sim.Width / Cell;
    public const int Rows = Sim.Height / Cell;
    public const float CoreRadius = 46f;
    public const float CorePadRadius = 58f;

    /// <summary>Depth of the buildable lip along the surface of the mass.</summary>
    public const float HighGroundDepth = 26f;

    /// <summary>Share of the field the dark mass has to cover, before the layout is accepted.</summary>
    private const float MinimumCoverage = 0.75f;
    private const float MaximumCoverage = 0.90f;

    /// <summary>The lattice the coverage and the walk to the border are measured on.</summary>
    private const int MeasureStep = 12;

    public static readonly Vector2 CoreCenter = new(Sim.Width * 0.5f, (Sim.Height * 0.5f) + 8f);

    private readonly Random _random;
    private readonly Lane[] _lanes;
    private readonly float _laneHalfWidth;
    private readonly Vector2[] _mouths;

    private byte[]? _image;

    /// <summary>Builds the arena for a seed. The same seed always gives the same field.</summary>
    public Terrain(int seed)
    {
        _random = new Random(seed);
        _lanes = DrawLanes();
        _laneHalfWidth = SolveLaneWidth();
        _mouths = FindMouths();
    }

    /// <summary>
    /// Where a molecule that is still outside the frame has to walk to get in: the nearest opening in
    /// the border. It is the lane it was queued at when it is still near it, and the nearest way in
    /// when it drifted, so a wave never walks into the mass and gets stuck outside.
    /// </summary>
    public Vector2 EntryPoint(Vector2 from)
    {
        var best = CoreCenter;
        var bestDistance = float.MaxValue;
        foreach (var mouth in _mouths)
        {
            var distance = Vector2.DistanceSquared(from, mouth);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = mouth;
            }
        }

        return best;
    }

    /// <summary>The open points around the border, sampled every few pixels: the ways in.</summary>
    private Vector2[] FindMouths()
    {
        const int Step = 8;
        const float Inset = 4f;
        var mouths = new List<Vector2>();
        for (var x = Inset; x < Sim.Width; x += Step)
        {
            TryAdd(new Vector2(x, Inset));
            TryAdd(new Vector2(x, Sim.Height - Inset));
        }

        for (var y = Inset; y < Sim.Height; y += Step)
        {
            TryAdd(new Vector2(Inset, y));
            TryAdd(new Vector2(Sim.Width - Inset, y));
        }

        return [.. mouths];

        void TryAdd(Vector2 point)
        {
            if (!Blocked(point.X, point.Y))
            {
                mouths.Add(point);
            }
        }
    }

    /// <summary>RGBA rows top-down, one byte per channel, baked once on first use.</summary>
    public byte[] Image => _image ??= Bake();

    /// <summary>Pixels from the nearest feature edge: positive in a lane, negative inside the mass.</summary>
    public float Distance(float x, float y) => -OpenDistance(x, y);

    public bool Blocked(float x, float y) => OpenDistance(x, y) > -1f;

    /// <summary>
    /// The raised lip along the surface of the mass: ground the horde cannot walk on and the only
    /// place an emitter can be mounted. Emitters stand above the lanes and fire down into them.
    /// </summary>
    public bool IsHighGround(float x, float y)
    {
        var distance = -OpenDistance(x, y);
        return distance < -4f && distance > -HighGroundDepth;
    }

    public static bool InCore(Vector2 point) =>
        Vector2.DistanceSquared(point, CoreCenter) < CoreRadius * CoreRadius;

    public static bool InCorePad(Vector2 point) =>
        Vector2.DistanceSquared(point, CoreCenter) < CorePadRadius * CorePadRadius;

    /// <summary>Share of the field covered by the mass; the complement of the lanes and the core pad.</summary>
    public float Coverage
    {
        get
        {
            var columns = (Sim.Width / MeasureStep) + 1;
            var rows = (Sim.Height / MeasureStep) + 1;
            var open = 0;
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < columns; column++)
                {
                    if (OpenDistance(column * MeasureStep, row * MeasureStep) < 0f)
                    {
                        open++;
                    }
                }
            }

            return 1f - (open / (float)(columns * rows));
        }
    }

    /// <summary>True when the open ground of the core still reaches the border.</summary>
    public bool HasLaneToTheBorder()
    {
        var columns = (Sim.Width / MeasureStep) + 1;
        var rows = (Sim.Height / MeasureStep) + 1;
        var open = new bool[columns * rows];
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                open[(row * columns) + column] = OpenDistance(column * MeasureStep, row * MeasureStep) < 0f;
            }
        }

        var start = ((int)(CoreCenter.Y / MeasureStep) * columns) + (int)(CoreCenter.X / MeasureStep);
        if (!open[start])
        {
            return false;
        }

        var reached = new bool[open.Length];
        var queue = new Queue<int>();
        reached[start] = true;
        queue.Enqueue(start);
        var mouths = 0;
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            var column = cell % columns;
            var row = cell / columns;
            if (column < 2 || row < 2 || column >= columns - 2 || row >= rows - 2)
            {
                mouths++;
            }

            for (var direction = 0; direction < 4; direction++)
            {
                var nextColumn = column + (direction == 0 ? 1 : direction == 1 ? -1 : 0);
                var nextRow = row + (direction == 2 ? 1 : direction == 3 ? -1 : 0);
                if (nextColumn < 0 || nextRow < 0 || nextColumn >= columns || nextRow >= rows)
                {
                    continue;
                }

                var next = (nextRow * columns) + nextColumn;
                if (reached[next] || !open[next])
                {
                    continue;
                }

                reached[next] = true;
                queue.Enqueue(next);
            }
        }

        // One mouth is a single-file trickle; a wave needs a few ways in to spread out.
        return mouths >= 6;
    }

    /// <summary>
    /// Signed distance to the nearest open ground: negative inside a lane or the core pad, positive
    /// inside the mass, in pixels.
    /// </summary>
    private float OpenDistance(float x, float y)
    {
        var point = new Vector2(x, y);
        var best = Vector2.Distance(point, CoreCenter) - CorePadRadius;
        foreach (var lane in _lanes)
        {
            best = MathF.Min(best, DistanceTo(lane, point) - _laneHalfWidth);
        }

        return best;
    }

    /// <summary>
    /// Distance to a lane's centreline, with the radius wobbled by noise so the wall is a carved
    /// surface rather than a drawn tube. The wobble is inside the wall, never across it, so a lane
    /// cannot be pinched shut by its own noise.
    /// </summary>
    private static float DistanceTo(in Lane lane, Vector2 point)
    {
        var start = lane.From;
        var end = lane.To;
        var span = end - start;
        var length = span.LengthSquared();
        var along = length > 0.0001f ? Math.Clamp(Vector2.Dot(point - start, span) / length, 0f, 1f) : 0f;
        var nearest = start + (span * along);
        var distance = Vector2.Distance(point, nearest);
        return distance + ((ValueNoise(point.X * 0.012f, point.Y * 0.012f) - 0.5f) * 14f);
    }

    /// <summary>
    /// Lanes from a border mouth to the core: a few segments each, jittered so no two runs of the
    /// same seed look alike. Every lane ends on the core pad by construction, so a path always exists.
    /// </summary>
    private Lane[] DrawLanes()
    {
        var lanes = new List<Lane>();
        var count = 4 + _random.Next(3);
        var start = _random.NextSingle() * MathF.Tau;
        for (var index = 0; index < count; index++)
        {
            var angle = start + (index * MathF.Tau / count) + ((_random.NextSingle() - 0.5f) * 0.5f);
            var mouth = CoreCenter + new Vector2(
                MathF.Cos(angle) * (Sim.Width * 0.75f),
                MathF.Sin(angle) * (Sim.Height * 0.75f));
            mouth.X = Math.Clamp(mouth.X, -20f, Sim.Width + 20f);
            mouth.Y = Math.Clamp(mouth.Y, -20f, Sim.Height + 20f);

            var previous = mouth;
            var segments = 3 + _random.Next(2);
            for (var step = 1; step <= segments; step++)
            {
                var t = step / (float)segments;
                var target = Vector2.Lerp(mouth, CoreCenter, t);
                if (step < segments)
                {
                    // Push the waypoint sideways so the lane snakes instead of running straight in.
                    var direction = Vector2.Normalize(CoreCenter - mouth);
                    var side = new Vector2(-direction.Y, direction.X);
                    target += side * ((_random.NextSingle() - 0.5f) * 220f);
                    target.X = Math.Clamp(target.X, 12f, Sim.Width - 12f);
                    target.Y = Math.Clamp(target.Y, 12f, Sim.Height - 12f);
                }
                else
                {
                    target = CoreCenter;
                }

                lanes.Add(new Lane(previous, target));
                previous = target;
            }
        }

        return [.. lanes];
    }

    /// <summary>
    /// Widens or narrows every lane until the mass covers the share of the field the game wants.
    /// A bisection on one number keeps the layout the seed drew and lands the coverage in range.
    /// </summary>
    private float SolveLaneWidth()
    {
        var low = 14f;
        var high = 64f;
        var width = 30f;
        for (var attempt = 0; attempt < 18; attempt++)
        {
            width = (low + high) * 0.5f;
            var coverage = MeasureCoverage(width);
            if (coverage > MaximumCoverage)
            {
                low = width;
            }
            else if (coverage < MinimumCoverage)
            {
                high = width;
            }
            else
            {
                return width;
            }
        }

        return width;
    }

    private float MeasureCoverage(float halfWidth)
    {
        var columns = (Sim.Width / MeasureStep) + 1;
        var rows = (Sim.Height / MeasureStep) + 1;
        var open = 0;
        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var point = new Vector2(column * MeasureStep, row * MeasureStep);
                var best = Vector2.Distance(point, CoreCenter) - CorePadRadius;
                foreach (var lane in _lanes)
                {
                    best = MathF.Min(best, DistanceTo(lane, point) - halfWidth);
                }

                if (best < 0f)
                {
                    open++;
                }
            }
        }

        return 1f - (open / (float)(columns * rows));
    }

    private byte[] Bake()
    {
        var pixels = new byte[Sim.Width * Sim.Height * 4];
        for (var y = 0; y < Sim.Height; y++)
        {
            for (var x = 0; x < Sim.Width; x++)
            {
                var (red, green, blue) = Shade(x, y, -OpenDistance(x, y));
                var index = ((y * Sim.Width) + x) * 4;
                pixels[index] = ToByte(red);
                pixels[index + 1] = ToByte(green);
                pixels[index + 2] = ToByte(blue);
                pixels[index + 3] = 255;
            }
        }

        PaintCorePad(pixels);
        return pixels;
    }

    private static (float Red, float Green, float Blue) Shade(int x, int y, float distance)
    {
        if (distance < -6f)
        {
            return DarkMatter(x, y, distance);
        }

        if (distance < 0f)
        {
            // The lit rim every mass shares: a thin shell of plasma where the wall meets the lane.
            var grain = 0.78f + (0.30f * ValueNoise(x * 0.09f, y * 0.09f));
            return (0.96f * grain, 0.60f * grain, 0.80f * grain);
        }

        if (distance < 5f)
        {
            // The halo bleeding off that rim into the dust the horde walks through.
            var fade = 1f - (distance / 5f);
            return ((0.30f * fade) + 0.02f, (0.13f * fade) + 0.02f, (0.26f * fade) + 0.04f);
        }

        return Void(x, y);
    }

    private static (float Red, float Green, float Blue) DarkMatter(int x, int y, float distance)
    {
        // A dark mass with a moonlit shelf along its surface: that lit band is the buildable ground.
        var coarse = ValueNoise(x * 0.012f, y * 0.012f);
        var fine = ValueNoise(x * 0.07f, y * 0.07f);
        var shade = 0.55f + (0.55f * coarse) + (0.25f * fine);
        if (distance > -HighGroundDepth)
        {
            return (0.30f * shade, 0.27f * shade, 0.42f * shade);
        }

        return (0.09f * shade, 0.06f * shade, 0.15f * shade);
    }

    private static (float Red, float Green, float Blue) Void(int x, int y)
    {
        // The lane itself: near-black dust with the occasional star.
        var cloud = ValueNoise(x * 0.0035f, y * 0.003f);
        var shade = 0.55f + (0.45f * cloud);
        var star = Hash(x, y);
        if (star > 0.9989f)
        {
            return (0.95f, 0.92f, 1f);
        }

        if (star > 0.9975f)
        {
            return (0.55f, 0.48f, 0.75f);
        }

        return (0.035f * shade, 0.022f * shade, 0.070f * shade);
    }

    private static void PaintCorePad(byte[] pixels)
    {
        var center = CoreCenter;
        var radius = (int)CorePadRadius + 10;
        for (var y = (int)center.Y - radius; y <= (int)center.Y + radius; y++)
        {
            if (y < 0 || y >= Sim.Height)
            {
                continue;
            }

            for (var x = (int)center.X - radius; x <= (int)center.X + radius; x++)
            {
                if (x < 0 || x >= Sim.Width)
                {
                    continue;
                }

                var dx = x - center.X;
                var dy = y - center.Y;
                var distance = MathF.Sqrt((dx * dx) + (dy * dy));
                if (distance > CorePadRadius + 8f)
                {
                    continue;
                }

                var index = ((y * Sim.Width) + x) * 4;
                if (distance > CorePadRadius)
                {
                    var fade = 1f - ((distance - CorePadRadius) / 8f);
                    Blend(pixels, index, 0.55f, 0.28f, 0.52f, 0.30f * fade);
                    continue;
                }

                if (distance > CorePadRadius - 5f)
                {
                    Set(pixels, index, 0.72f, 0.58f, 0.78f);
                    continue;
                }

                var blockX = x / 9;
                var blockY = y / 8;
                var hash = Hash(blockX + 17, blockY - 3);
                var gap = x % 9 == 0 || y % 8 == 0;
                var shade = (0.62f + (0.38f * hash)) * (gap ? 0.55f : 1f);
                Set(pixels, index, 0.10f * shade, 0.08f * shade, 0.16f * shade);
            }
        }
    }

    private static void Blend(byte[] pixels, int index, float red, float green, float blue, float alpha)
    {
        var inverse = 1f - alpha;
        pixels[index] = ToByte(((pixels[index] / 255f) * inverse) + (red * alpha));
        pixels[index + 1] = ToByte(((pixels[index + 1] / 255f) * inverse) + (green * alpha));
        pixels[index + 2] = ToByte(((pixels[index + 2] / 255f) * inverse) + (blue * alpha));
        pixels[index + 3] = 255;
    }

    private static void Set(byte[] pixels, int index, float red, float green, float blue)
    {
        pixels[index] = ToByte(red);
        pixels[index + 1] = ToByte(green);
        pixels[index + 2] = ToByte(blue);
        pixels[index + 3] = 255;
    }

    private static byte ToByte(float value) => (byte)Math.Clamp((int)(value * 255f), 0, 255);

    private static float Hash(int x, int y)
    {
        var hash = (uint)((x * 374761393) + (y * 668265263));
        hash = (hash ^ (hash >> 13)) * 1274126177u;
        return ((hash ^ (hash >> 16)) & 0xFFFFFF) / 16777215f;
    }

    private static float ValueNoise(float x, float y)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        var fx = x - x0;
        var fy = y - y0;
        var sx = fx * fx * (3f - (2f * fx));
        var sy = fy * fy * (3f - (2f * fy));
        var top = Hash(x0, y0) + ((Hash(x0 + 1, y0) - Hash(x0, y0)) * sx);
        var bottom = Hash(x0, y0 + 1) + ((Hash(x0 + 1, y0 + 1) - Hash(x0, y0 + 1)) * sx);
        return top + ((bottom - top) * sy);
    }

    /// <summary>One straight run of a lane's centreline.</summary>
    private readonly record struct Lane(Vector2 From, Vector2 To);
}
