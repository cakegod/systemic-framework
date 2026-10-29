using System.Numerics;

namespace MoleculeSwarm;

internal readonly record struct Tracer(Vector2 From, Vector2 To, float Life, float MaxLife, float Width);

internal readonly record struct Boom(Vector2 Center, float Radius, float Life, float MaxLife);

/// <summary>A ground stain: blood or blast scorch. Append-only, drawn as fading point sprites.</summary>
internal struct Splat
{
    public Vector2 Position;
    public float Radius;
    public float Tint;
}

/// <summary>
/// Everything that is drawn but never simulates: blood and scorch on the ground, tracers, and
/// blast rings. All of it is append-only and trimmed by age, so the cost of a busy tick is a write.
/// </summary>
internal sealed class Effects
{
    public const int MaxBlood = 9_000;
    public const int MaxScorch = 6_000;
    public const int MaxTracers = 400;
    public const int MaxBooms = 96;

    private readonly Splat[] _blood = new Splat[MaxBlood];
    private int _bloodCount;
    private readonly Splat[] _scorch = new Splat[MaxScorch];
    private int _scorchCount;
    private readonly Tracer[] _tracers = new Tracer[MaxTracers];
    private int _tracerCount;
    private readonly Boom[] _booms = new Boom[MaxBooms];
    private int _boomCount;

    private readonly Random _random;

    public Effects(Random random) => _random = random;

    public int BloodCount => _bloodCount;

    public Splat[] Blood => _blood;

    public int ScorchCount => _scorchCount;

    public Splat[] Scorch => _scorch;

    public int TracerCount => _tracerCount;

    public Tracer TracerAt(int index) => _tracers[index];

    public int BoomCount => _boomCount;

    public Boom BoomAt(int index) => _booms[index];

    /// <summary>A spray of blood where an orc fell.</summary>
    public void AddBlood(Vector2 position, float radius)
    {
        if (_bloodCount >= MaxBlood)
        {
            return;
        }

        _blood[_bloodCount++] = new Splat
        {
            Position = position + new Vector2(
                (_random.NextSingle() - 0.5f) * radius,
                (_random.NextSingle() - 0.5f) * radius),
            Radius = radius * (0.7f + (_random.NextSingle() * 0.8f)),
            Tint = _random.NextSingle(),
        };
    }

    /// <summary>Golden blast residue scattered over a radius.</summary>
    public void AddScorch(Vector2 center, float radius, int count)
    {
        for (var step = 0; step < count && _scorchCount < MaxScorch; step++)
        {
            var angle = _random.NextSingle() * MathF.Tau;
            var distance = MathF.Sqrt(_random.NextSingle()) * radius;
            _scorch[_scorchCount++] = new Splat
            {
                Position = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * distance,
                Radius = 6f + (_random.NextSingle() * 18f),
                Tint = _random.NextSingle(),
            };
        }
    }

    public void AddTracer(Vector2 from, Vector2 to, float life, float width)
    {
        if (_tracerCount < MaxTracers)
        {
            _tracers[_tracerCount++] = new Tracer(from, to, life, life, width);
        }
    }

    public void AddBoom(Vector2 center, float radius)
    {
        if (_boomCount < MaxBooms)
        {
            _booms[_boomCount++] = new Boom(center, radius, 0.34f, 0.34f);
        }
    }

    public void Update(float dt)
    {
        for (var index = _tracerCount - 1; index >= 0; index--)
        {
            var tracer = _tracers[index];
            tracer = tracer with { Life = tracer.Life - dt };
            if (tracer.Life <= 0f)
            {
                _tracers[index] = _tracers[--_tracerCount];
            }
            else
            {
                _tracers[index] = tracer;
            }
        }

        for (var index = _boomCount - 1; index >= 0; index--)
        {
            var boom = _booms[index];
            boom = boom with { Life = boom.Life - dt };
            if (boom.Life <= 0f)
            {
                _booms[index] = _booms[--_boomCount];
            }
            else
            {
                _booms[index] = boom;
            }
        }
    }
}
