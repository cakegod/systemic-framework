using System.Numerics;

namespace GlowGarden;

public enum Talent { Echo, Seedstorm, Magnetism, ChainReaction, LivingProjectile, Clone }

public readonly record struct PendingImpact(Vector2 Position, int DueTick, int ExpiryTick, bool IsSeed);

public sealed partial class GardenSimulation
{
    public const int MaxPendingImpacts = 256;
    public const int MaxReactionBursts = 16;
    public const int CopyLifetime = 180;
    private readonly bool[] _talents = new bool[6];
    private readonly long[] _effects = new long[6];
    private readonly List<PendingImpact> _pending = new(MaxPendingImpacts);
    private readonly Dictionary<int, int> _copyExpiry = new(MaxCopies);
    private readonly int[] _reactionTargets = new int[MaxReactionBursts];
    private readonly int[] _reactionDepths = new int[MaxReactionBursts];

    public int ActiveTalentCount { get; private set; }
    public int CopyCount => _copyExpiry.Count;
    public int PendingImpactCount => _pending.Count;
    public IReadOnlyList<PendingImpact> PendingImpacts { get; }
    public bool IsTalentActive(Talent talent) => _talents[TalentIndex(talent)];
    public long EffectCount(Talent talent) => _effects[TalentIndex(talent)];

    private static int TalentIndex(Talent talent) => (uint)talent < 6 ? (int)talent : throw new ArgumentOutOfRangeException(nameof(talent));

    public bool ToggleTalent(Talent talent)
    {
        var index = TalentIndex(talent);
        if (!_talents[index] && ActiveTalentCount == 3) return false;
        _talents[index] = !_talents[index];
        ActiveTalentCount += _talents[index] ? 1 : -1;
        return true;
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
            if (c.IsCopy && _copyExpiry[c.Id] <= Tick)
            {
                // Copies drop loads only if future creature kinds are added; currently all copies are Burstlings.
                _copyExpiry.Remove(c.Id);
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
                    _plants.Add(new(_plants.Count, impact.Position, 0));
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
                    SpawnCopy(impact.Position);
                    _pending.RemoveAt(i);
                }
                else if (Tick >= impact.ExpiryTick) _pending.RemoveAt(i);
                else i++;
            }
        }
        if (!IsTalentActive(Talent.Magnetism)) return;
        for (var i = 0; i < _motes.Count; i++)
        {
            var m = _motes[i];
            var target = -1;
            var best = 90f * 90;
            for (var j = 0; j < _creatures.Count; j++)
            {
                var c = _creatures[j];
                if (c.Kind != CreatureKind.Gatherer || c.CarriedGlow >= 3 + Rank(UpgradeKind.GathererCapacity)) continue;
                var distance = Vector2.DistanceSquared(m.Position, c.Position);
                if (distance > 0 && distance < best) { target = j; best = distance; }
            }
            if (target < 0) continue;
            _motes[i] = m with { Position = Move(m.Position, _creatures[target].Position, 4) };
            _effects[(int)Talent.Magnetism]++;
        }
    }

    private void SpawnCopy(Vector2 position)
    {
        if (!IsTalentActive(Talent.Clone) || CopyCount >= MaxCopies) return;
        var id = _nextCreatureId++;
        _creatures.Add(new(id, CreatureKind.Burstling, position, 0, 15, true));
        _copyExpiry.Add(id, Tick + CopyLifetime);
        _effects[(int)Talent.Clone]++;
    }

    private Vector2? BurstWithTalents(int index, bool allowBounce)
    {
        var count = 1;
        _reactionTargets[0] = index;
        _reactionDepths[0] = 0;
        Vector2? bouncePosition = null;
        var bounced = false;
        for (var head = 0; head < count; head++)
        {
            var target = _reactionTargets[head];
            if (_plants[target].Growth < 1) continue;
            var position = _plants[target].Position;
            BurstPlant(target);
            if (head > 0)
            {
                _effects[(int)Talent.ChainReaction]++;
                SpawnCopy(position);
            }
            if (IsTalentActive(Talent.Echo)) QueueImpact(position, false);
            if (IsTalentActive(Talent.Seedstorm))
            {
                var angle = NextFloat() * MathF.Tau;
                QueueImpact(Vector2.Clamp(position + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 50, Vector2.Zero, new(Width, Height)), true);
            }
            if (IsTalentActive(Talent.ChainReaction) && _reactionDepths[head] < 3)
            {
                for (var j = 0; j < _plants.Count && count < MaxReactionBursts; j++)
                {
                    if (_plants[j].Growth < 1 || Vector2.DistanceSquared(position, _plants[j].Position) > 100 * 100) continue;
                    var queued = false;
                    for (var k = 0; k < count; k++) if (_reactionTargets[k] == j) { queued = true; break; }
                    if (queued) continue;
                    _reactionTargets[count] = j;
                    _reactionDepths[count++] = _reactionDepths[head] + 1;
                }
            }
            if (!bounced && allowBounce && IsTalentActive(Talent.LivingProjectile))
            {
                var bounce = NearestRipe(position, 240);
                if (bounce >= 0)
                {
                    BurstPlant(bounce);
                    _effects[(int)Talent.LivingProjectile]++;
                    SpawnCopy(_plants[bounce].Position);
                    bouncePosition = _plants[bounce].Position;
                    bounced = true;
                }
            }
        }
        return bouncePosition;
    }
}
