using System.Numerics;
using Systemic.Kernel;
using Systemic.Rules;

namespace GlowGarden;

public enum Talent { Echo, Seedstorm, Magnetism, ChainReaction, LivingProjectile, Clone }

public readonly record struct PendingImpact(Vector2 Position, int DueTick, int ExpiryTick, bool IsSeed);

public sealed partial class GardenSimulation
{
    public const int MaxPendingImpacts = 256;
    public const int MaxReactionBursts = 16;
    public const int CopyLifetime = 180;
    public const int MaxTalentIntents = 8192;
    private readonly long[] _effects = new long[6];
    private readonly List<PendingImpact> _pending = new(MaxPendingImpacts);
    private readonly Queue<TalentIntent> _talentIntents = new(MaxTalentIntents);
    private readonly PopulationSpatialGrid2D _creatureGrid = new(new Bounds2(Width / 2, Height / 2, Width / 2, Height / 2), 90);
    private readonly List<int> _nearbyCreatures = new(MaxWorkers + MaxCopies);
    private Impact? _publishingImpact;

    // Shared by one worker impact and its finite breadth-first chain, never indexed by dense rows.
    private sealed class Reaction(int creatureId, bool allowBounce)
    {
        public int CreatureId { get; } = creatureId;
        public bool AllowBounce { get; } = allowBounce;
        public int Bursts { get; set; } = 1;
        public bool Bounced { get; set; }
    }

    private readonly record struct Impact(Vector2 Position, Reaction? Reaction = null, int Depth = 0);
    private readonly record struct TalentIntent(Talent Talent, Impact Impact);

    public int ActiveTalentCount => Enum.GetValues<Talent>().Count(IsTalentActive);
    public int CopyCount => _creatures.Count(creature => creature.IsCopy);
    public int PendingImpactCount => _pending.Count;
    public IReadOnlyList<PendingImpact> PendingImpacts { get; }
    public bool IsTalentActive(Talent talent) => _garden.HasTag(TalentTag(talent));
    public long EffectCount(Talent talent) => _effects[TalentIndex(talent)];

    private static int TalentIndex(Talent talent) => (uint)talent < 6 ? (int)talent : throw new ArgumentOutOfRangeException(nameof(talent));
    private static string TalentTag(Talent talent)
    {
        _ = TalentIndex(talent);
        return talent switch
        {
            Talent.Echo => "garden.talent.echo",
            Talent.Seedstorm => "garden.talent.seedstorm",
            Talent.Magnetism => "garden.talent.magnetism",
            Talent.ChainReaction => "garden.talent.chain",
            Talent.LivingProjectile => "garden.talent.projectile",
            _ => "garden.talent.clone"
        };
    }

    public bool ToggleTalent(Talent talent)
    {
        var tag = TalentTag(talent);
        if (_garden.HasTag(tag)) _garden.RemoveTag(tag);
        else
        {
            if (ActiveTalentCount == 3) return false;
            _garden.AddTag(tag);
        }
        return true;
    }

    /// <summary>Register with the host builder. Rules select intents; only successful commits enqueue them.</summary>
    public void ObserveFact(SimulationFact fact, SimulationFactEmitter emitter)
    {
        if (_publishingImpact is not { } impact || fact.SourceId != _garden.Id) return;
        Talent? talent = fact.Type switch
        {
            "garden.talent.echo" => Talent.Echo,
            "garden.talent.seedstorm" => Talent.Seedstorm,
            "garden.talent.magnetism" => Talent.Magnetism,
            "garden.talent.chain" => Talent.ChainReaction,
            "garden.talent.projectile" => Talent.LivingProjectile,
            "garden.talent.clone" => Talent.Clone,
            _ => null
        };
        if (talent is not { } selected) return;
        emitter.OnCommit(() =>
        {
            if (_talentIntents.Count < MaxTalentIntents) _talentIntents.Enqueue(new(selected, impact));
        });
    }

    private void Publish(string type, double amount = 0, Impact? impact = null)
    {
        var previous = _publishingImpact;
        _publishingImpact = impact;
        try
        {
            _stepContext!.Publish(new SimulationFact(type, _garden.Id, _garden.Id, Amount: amount));
        }
        finally { _publishingImpact = previous; }
    }

    private int NearestRipe(Vector2 position, float radius)
    {
        var target = -1;
        var best = radius * radius;
        for (var i = 0; i < _plants.Count; i++)
        {
            var distance = Vector2.DistanceSquared(position, _plants[i].Position);
            if (_plants[i].Growth >= 1 && distance < best) { target = i; best = distance; }
        }
        return target;
    }

    private void QueueImpact(Vector2 position, bool seed)
    {
        if (_pending.Count < MaxPendingImpacts)
            _pending.Add(new(position, Tick + (seed ? 18 : 12), Tick + 60, seed));
    }

    private void StepTalents()
    {
        for (var i = _creatures.Count - 1; i >= 0; i--)
        {
            var c = _creatures[i];
            if (c.IsCopy && _creatures.Columns.CopyExpiryTick.ReadOnlySpan[i] <= Tick)
            {
                _creatures.RemoveAt(i);
            }
        }
        for (var i = 0; i < _pending.Count;)
        {
            var impact = _pending[i];
            if (Tick < impact.DueTick) { i++; continue; }
            if (impact.IsSeed)
            {
                if (_plants.Count < MaxPlants)
                {
                    // Stable identifiers remain independent of row count and swap-back layout.
                    _nextPlantId = Math.Max(_nextPlantId, _plants.Count == 0 ? 0 : _plants.Max(p => p.Id) + 1);
                    _plants.Add(new(_nextPlantId++, impact.Position, 0));
                    _effects[(int)Talent.Seedstorm]++;
                }
                _pending.RemoveAt(i);
            }
            else
            {
                var target = NearestRipe(impact.Position, 180);
                if (target >= 0)
                {
                    BurstPlant(target);
                    _effects[(int)Talent.Echo]++;
                    Publish("garden.combo", impact: new Impact(impact.Position));
                    _pending.RemoveAt(i);
                }
                else if (Tick >= impact.ExpiryTick) _pending.RemoveAt(i);
                else i++;
            }
        }
        Publish("garden.tick", impact: new Impact(Colony));
    }

    private void ApplyMagnetism()
    {
        // This snapshot is local to a phase with no creature movement or structural edits.
        // Mote removals elsewhere cannot stale its creature row indexes.
        _creatureGrid.Rebuild(_creatures.Columns.Position.ReadOnlySpan);
        for (var i = 0; i < _motes.Count; i++)
        {
            var m = _motes[i];
            var target = -1;
            var best = 90f * 90;
            _creatureGrid.QueryRadius(m.Position, 90, _nearbyCreatures);
            foreach (var row in _nearbyCreatures)
            {
                var c = _creatures[row];
                if (c.Kind != CreatureKind.Gatherer || c.CarriedGlow >= 3 + Rank(UpgradeKind.GathererCapacity)) continue;
                var distance = Vector2.DistanceSquared(m.Position, c.Position);
                if (distance > 0 && distance < best) { target = row; best = distance; }
            }
            if (target < 0) continue;
            _motes[i] = m with { Position = Move(m.Position, _creatures[target].Position, 4) };
            _effects[(int)Talent.Magnetism]++;
        }
    }

    private void SpawnCopy(Vector2 position)
    {
        if (CopyCount >= MaxCopies) return;
        var id = _nextCreatureId++;
        _creatures.Add(new(id, CreatureKind.Burstling, position, 0, 15, true));
        _creatures.Columns.CopyExpiryTick.EditRange(_creatures.Count - 1, 1)[0] = Tick + CopyLifetime;
        _effects[(int)Talent.Clone]++;
    }

    private void DrainTalentIntents()
    {
        // Invoked after the host transaction commits, outside RuleEngine dispatch. Subsequent
        // publishes may enqueue more work, but cannot recursively execute domain effects.
        for (var processed = 0; processed < MaxTalentIntents && _talentIntents.TryDequeue(out var intent); processed++)
        {
            var impact = intent.Impact;
            switch (intent.Talent)
            {
                case Talent.Echo:
                    QueueImpact(impact.Position, false);
                    break;
                case Talent.Seedstorm:
                    var angle = NextFloat() * MathF.Tau;
                    QueueImpact(Vector2.Clamp(impact.Position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 50,
                        Vector2.Zero, new(Width, Height)), true);
                    break;
                case Talent.Magnetism:
                    ApplyMagnetism();
                    break;
                case Talent.Clone:
                    SpawnCopy(impact.Position);
                    break;
                case Talent.ChainReaction:
                    ApplyChain(impact);
                    break;
                case Talent.LivingProjectile:
                    ApplyBounce(impact);
                    break;
            }
        }
        _talentIntents.Clear();
    }

    private void ApplyChain(Impact impact)
    {
        if (impact.Reaction is not { } reaction || impact.Depth >= 3) return;
        for (var row = 0; row < _plants.Count && reaction.Bursts < MaxReactionBursts; row++)
        {
            var plant = _plants[row];
            if (plant.Growth < 1 || Vector2.DistanceSquared(impact.Position, plant.Position) > 100 * 100) continue;
            reaction.Bursts++;
            BurstPlant(row, new Impact(plant.Position, reaction, impact.Depth + 1));
            _effects[(int)Talent.ChainReaction]++;
            Publish("garden.combo", impact: new Impact(plant.Position));
        }
    }

    private void ApplyBounce(Impact impact)
    {
        if (impact.Reaction is not { AllowBounce: true, Bounced: false } reaction) return;
        var target = NearestRipe(impact.Position, 240);
        if (target < 0) return;
        var position = _plants[target].Position;
        BurstPlant(target);
        reaction.Bounced = true;
        _effects[(int)Talent.LivingProjectile]++;
        for (var row = 0; row < _creatures.Count; row++)
        {
            if (_creatures[row].Id != reaction.CreatureId) continue;
            _creatures[row] = _creatures[row] with { Position = position, LeapProgress = 1 };
            break;
        }
        Publish("garden.combo", impact: new Impact(position));
    }
}
