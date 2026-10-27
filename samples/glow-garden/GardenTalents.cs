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
    public long RejectedSeedSpawns { get; private set; }
    public IReadOnlyList<PendingImpact> PendingImpacts { get; }
    public bool IsTalentActive(Talent talent) => _garden.HasTag(TalentTag(talent));

    /// <summary>Reactions selected by the authored talent rules, counted by their adjustProperty effects.</summary>
    public long EffectCount(Talent talent) =>
        _garden.TryGetProperty(_world, TalentTag(talent) + ".uses", out var value) ? (long)value : 0;

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
        if (_garden.HasTag(tag))
        {
            CaptureCommandRollback();
            _garden.RemoveTag(tag);
        }
        else
        {
            if (ActiveTalentCount >= _tuning.TalentSlots) return false;
            CaptureCommandRollback();
            _garden.AddTag(tag);
        }
        return true;
    }

    /// <summary>Register with the host builder. Rules select intents; only successful commits enqueue them.</summary>
    public void ObserveFact(SimulationFact fact, SimulationFactEmitter emitter)
    {
        if (fact.SourceId != _garden.Id) return;
        // Keep subpixel simulation coordinates for the local reaction; the fact bridge stores a
        // stable integer-pixel snapshot for rules, replay, aggregation, and presentation.
        var impact = _publishingImpact
            ?? (fact.Position is { } position
                ? new Impact(new Vector2(position.X, position.Y))
                : _world.TryGetSpatialBounds(fact.TargetId, out var bounds)
                    ? new Impact(new Vector2(bounds.X, bounds.Y))
                    : null);
        if (impact is not { } anchor) return;
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
        // Enqueue immediately: the domain drain runs at the end of the step, before the
        // host's outer transaction commits, so deferred commit callbacks would arrive too late.
        if (_talentIntents.Count < MaxTalentIntents) _talentIntents.Enqueue(new(selected, anchor));
    }

    private void Publish(string type, double amount = 0, Impact? impact = null)
    {
        var previous = _publishingImpact;
        _publishingImpact = impact;
        try
        {
            SimulationFactPosition? position = impact is { } at
                ? new SimulationFactPosition(
                    checked((long)Math.Round(at.Position.X, MidpointRounding.ToEven)),
                    checked((long)Math.Round(at.Position.Y, MidpointRounding.ToEven)))
                : null;
            _publish(new SimulationFact(type, _garden.Id, _garden.Id, Amount: amount, Position: position));
        }
        finally { _publishingImpact = previous; }
    }

    private void QueueImpact(Vector2 position, bool seed)
    {
        var delay = seed ? _tuning.SeedstormDelay : _tuning.EchoDelay;
        var expiry = Math.Max(delay, seed ? _tuning.SeedstormExpiry : _tuning.EchoExpiry);
        if (_pending.Count < MaxPendingImpacts)
            _pending.Add(new(position, Tick + delay, Tick + expiry, seed));
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
                }
                _pending.RemoveAt(i);
            }
            else
            {
                var target = NearestRipe(impact.Position, _tuning.EchoRadius);
                if (target >= 0)
                {
                    BurstPlant(target, anchor: impact.Position);
                    Publish("garden.combo", impact: new Impact(impact.Position));
                    _pending.RemoveAt(i);
                }
                else if (Tick >= impact.ExpiryTick) _pending.RemoveAt(i);
                else i++;
            }
        }
    }

    private void ApplyMagnetism()
    {
        var radius = _tuning.MagnetismRadius;
        var pull = _tuning.MagnetismPull;
        // This snapshot is local to a phase with no creature movement or structural edits.
        // Mote removals elsewhere cannot stale its creature row indexes.
        _creatureGrid.Rebuild(_creatures.Columns.Position.ReadOnlySpan);
        for (var i = 0; i < _motes.Count; i++)
        {
            var m = _motes[i];
            var target = -1;
            var best = radius * radius;
            _creatureGrid.QueryRadius(m.Position, radius, _nearbyCreatures);
            foreach (var row in _nearbyCreatures)
            {
                var c = _creatures[row];
                if (c.Kind != CreatureKind.Gatherer || c.CarriedGlow >= 3 + Rank(UpgradeKind.GathererCapacity)) continue;
                var distance = Vector2.DistanceSquared(m.Position, c.Position);
                if (distance > 0 && distance < best) { target = row; best = distance; }
            }
            if (target < 0) continue;
            _motes[i] = m with { Position = Move(m.Position, _creatures[target].Position, pull) };
        }
    }

    private void SpawnCopy(Vector2 position)
    {
        if (CopyCount >= MaxCopies) return;
        var id = _nextCreatureId++;
        _creatures.Add(new(id, CreatureKind.Burstling, position, 0, 15, true));
        _creatures.Columns.CopyExpiryTick.EditRange(_creatures.Count - 1, 1)[0] =
            Tick + _tuning.CloneLifetime;
    }

    private void DrainTalentIntents()
    {
        // Invoked before the host transaction commits, outside RuleEngine dispatch. Subsequent
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
                    // Seeds drift anywhere in the garden, keeping only a minimum distance from the parent
                    // and never settling inside a thicket, where workers could not reach the pod.
                    var minimum = _tuning.SeedstormOffset;
                    var spot = impact.Position;
                    for (var attempt = 0; attempt < 12; attempt++)
                    {
                        spot = new Vector2(
                            Math.Clamp(NextFloat() * Width, 60, Width - 60),
                            Math.Clamp(NextFloat() * Height, 60, Height - 60));
                        var delta = spot - impact.Position;
                        var distance = delta.Length();
                        if (distance < minimum)
                        {
                            var direction = distance < 0.01f
                                ? new Vector2(MathF.Cos(NextFloat() * MathF.Tau), MathF.Sin(NextFloat() * MathF.Tau))
                                : delta / distance;
                            spot = Vector2.Clamp(impact.Position + (direction * minimum),
                                new(60, 60), new(Width - 60, Height - 60));
                        }

                        if (!Obstacles.IsBlocked(spot)
                            && Vector2.DistanceSquared(spot, impact.Position) >= minimum * minimum) break;
                    }
                    if ((Obstacles.IsBlocked(spot)
                            || Vector2.DistanceSquared(spot, impact.Position) < minimum * minimum)
                        && !Obstacles.TryFindFreePosition(spot, impact.Position, minimum, out spot))
                    {
                        RejectedSeedSpawns++;
                        break;
                    }

                    QueueImpact(spot, true);
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
        var radius = _tuning.ChainRadius;
        var limit = Math.Min(_tuning.ChainBursts, MaxReactionBursts);
        _plantGrid.QueryRadius(impact.Position, radius, _reactionPlants);
        var growth = _plants.Columns.Growth.ReadOnlySpan;
        var positions = _plants.Columns.Position.ReadOnlySpan;
        foreach (var row in _reactionPlants)
        {
            if (reaction.Bursts >= limit) break;
            var position = positions[row];
            if (growth[row] < 1 || Vector2.DistanceSquared(impact.Position, position) > radius * radius) continue;
            reaction.Bursts++;
            BurstPlant(row, new Impact(position, reaction, impact.Depth + 1));
            Publish("garden.combo", impact: new Impact(position));
        }
    }

    private void ApplyBounce(Impact impact)
    {
        if (impact.Reaction is not { AllowBounce: true, Bounced: false } reaction) return;
        var target = NearestRipe(impact.Position, _tuning.ProjectileRadius);
        if (target < 0) return;
        var position = _plants[target].Position;
        BurstPlant(target, anchor: position);
        reaction.Bounced = true;
        for (var row = 0; row < _creatures.Count; row++)
        {
            if (_creatures[row].Id != reaction.CreatureId) continue;
            _creatures[row] = _creatures[row] with { Position = position, LeapProgress = 1 };
            break;
        }
        Publish("garden.combo", impact: new Impact(position));
    }
}
