using System.Numerics;

using Systemic.Host;
using Systemic.Kernel;

namespace GlowGarden;

public readonly record struct PlantState(int Id, Vector2 Position, float Growth);

public readonly record struct CreatureState(int Id, CreatureKind Kind, Vector2 Position, int CarriedGlow, int Cooldown, bool IsCopy, float LeapProgress = 0);

public readonly record struct MoteState(int Id, Vector2 Position, Vector2 Velocity, int Value);

/// <summary>
/// Live plants, creatures (including copy expiry), and motes are authoritative Kernel populations.
/// Progression, identifiers, diagnostics, delayed effects, and commit-phase talent work are not a
/// complete save/rollback implementation; the prototype deliberately does not support save/load.
/// </summary>
public sealed partial class GardenSimulation : ISystemicSystem
{
    public const int MaxWorkers = 1000, MaxPlants = 64, MaxMotes = 12000, MaxCopies = 128;

    public const float Width = 960, Height = 640;

    public static readonly Vector2 Colony = new(480, 320);

    private readonly World _world;
    private readonly Entity _garden;
    internal readonly PlantStates _plants;
    internal readonly CreatureStates _creatures;
    internal readonly MoteStates _motes;
    private SystemicStepContext? _stepContext;
    private int _nextCreatureId, _nextMoteId, _nextPlantId;

    public GardenSimulation(int seed)
        : this(SystemicGame.FromDirectory(Path.Combine(AppContext.BaseDirectory, "content"), seed).World) { }

    public GardenSimulation(World world)
    {
        _world = world;
        _garden = world.Entities.Single(e => e.HasTag("garden"));
        _plants = new PlantStates(world);
        _creatures = new CreatureStates(world);
        _motes = new MoteStates(world);
        _creatureGrid.Reserve(MaxWorkers + MaxCopies);
        Plants = _plants;
        Creatures = _creatures;
        Motes = _motes;
        PendingImpacts = _pending.AsReadOnly();

        for (var i = 0; i < 6; i++)
        {
            var angle = i * MathF.Tau / 6;
            _plants.Add(new(_nextPlantId++, Colony + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 150, NextFloat()));
        }

        for (var i = 0; i < 6; i++) _creatures.Add(new(_nextCreatureId++, i < 2 ? CreatureKind.Burstling : CreatureKind.Gatherer, Colony, 0, 0, false));
    }

    public string Id => "glow-garden";

    public int Order => 0;

    public IReadOnlyList<PlantState> Plants { get; }

    public IReadOnlyList<CreatureState> Creatures { get; }

    public IReadOnlyList<MoteState> Motes { get; }

    public long BurstCount { get; private set; }

    public int Tick { get; private set; }

    private float NextFloat() => _world.Draws.NextFloat();

    private static Vector2 Move(Vector2 from, Vector2 to, float distance)
    {
        var delta = to - from;
        var length = delta.Length();
        return length <= distance ? to : from + delta * (distance / length);
    }

    public void Step(SystemicStepContext context)
    {
        _stepContext = context;
        Tick = context.Tick;
        StepTalents();

        for (var i = 0; i < _plants.Count; i++)
        {
            var p = _plants[i];
            _plants[i] = p with
            {
                Growth = MathF.Min(1, p.Growth + (1 + Rank(UpgradeKind.PodGrowth) * 0.12f) / 240f)
            };
        }

        for (var i = 0; i < _motes.Count; i++)
        {
            var m = _motes[i];
            _motes[i] = m with
            {
                Position = Vector2.Clamp(m.Position + m.Velocity, Vector2.Zero, new(Width, Height)),
                Velocity = m.Velocity * 0.9f
            };
        }

        for (var i = 0; i < _creatures.Count; i++)
        {
            var c = _creatures[i];

            if (c.Kind == CreatureKind.Burstling)
            {
                if (c.Cooldown > 0)
                {
                    _creatures[i] = c with
                    {
                        Cooldown = c.Cooldown - 1,
                        LeapProgress = 0
                    };
                    continue;
                }

                var target = -1;
                var best = float.MaxValue;

                for (var j = 0; j < _plants.Count; j++) if (_plants[j].Growth >= 1)
                {
                    var d = Vector2.DistanceSquared(c.Position, _plants[j].Position);
                    if (d < best)
                    {
                        best = d;
                        target = j;
                    }
                }

                if (target >= 0)
                {
                    var p = _plants[target];
                    c = c with
                    {
                        Position = Move(c.Position, p.Position, 3),
                        LeapProgress = Math.Clamp(1 - MathF.Sqrt(best) / 48f, 0, 1)
                    };
                    if (Vector2.DistanceSquared(c.Position, p.Position) < 16)
                    {
                        BurstPlant(target, new Impact(p.Position, new Reaction(c.Id, !c.IsCopy)));
                        c = c with
                        {
                            Cooldown = Math.Max(3, 30 - Rank(UpgradeKind.BurstlingWorkRate))
                        };
                    }
                }
            }

            else
            {
                var capacity = 3 + Rank(UpgradeKind.GathererCapacity);
                var speed = 3 + Rank(UpgradeKind.GathererSpeed) * 0.3f;

                if (c.CarriedGlow >= capacity || (c.CarriedGlow > 0 && _motes.Count == 0))
                {
                    c = c with
                    {
                        Position = Move(c.Position, Colony, speed)
                    };

                    if (Vector2.DistanceSquared(c.Position, Colony) < 16)
                    {
                        Glow += c.CarriedGlow;
                        BankedGlow += c.CarriedGlow;
                        Publish("garden.glow.deposited", c.CarriedGlow);
                        c = c with
                        {
                            CarriedGlow = 0
                        };
                    }
                }

                else if (_motes.Count > 0)
                {
                    var target = 0;
                    var best = float.MaxValue;

                    for (var j = 0; j < _motes.Count; j++)
                    {
                        var d = Vector2.DistanceSquared(c.Position, _motes[j].Position);
                        if (d < best)
                        {
                            best = d;
                            target = j;
                        }
                    }

                    var m = _motes[target];
                    c = c with
                    {
                        Position = Move(c.Position, m.Position, speed)
                    };

                    if (Vector2.DistanceSquared(c.Position, m.Position) < 25)
                    {
                        c = c with
                        {
                            CarriedGlow = c.CarriedGlow + m.Value
                        };
                        _motes.RemoveAt(target);
                        Publish("garden.glow.collected", m.Value);
                    }
                }
            }

            _creatures[i] = c;
        }

        // Fact callbacks enqueue only on successful outer commit. Drain after those callbacks,
        // so reactions are visible when Game.Step returns and never reenter active dispatch.
        using var completion = _world.BeginTransaction();
        completion.OnCommit(DrainTalentIntents);
        completion.Commit();
    }

    private void BurstPlant(int index, Impact? impact = null)
    {
        var p = _plants[index];
        if (p.Growth < 1) return;

        _plants[index] = p with
        {
            Growth = 0
        };
        BurstCount++;

        for (var j = 0; j < 8 + Rank(UpgradeKind.PodYield) * 2 && _motes.Count < MaxMotes; j++)
        {
            var angle = NextFloat() * MathF.Tau;
            var speed = 0.5f + NextFloat() * 2;
            _motes.Add(new(_nextMoteId++, p.Position, new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed, 1));
        }
        Publish(impact.HasValue ? "garden.pod.burst" : "garden.pod.secondary", p.Id, impact);
    }
}

