using System.Numerics;
using System.Text.Json;
using Systemic.Host;

namespace GlowGarden;

public sealed partial class GardenSimulation
{
    private static readonly JsonSerializerOptions s_stateJson = new() { IncludeFields = true };

    /// <summary>Captures the non-population state owned by the garden system.</summary>
    public JsonElement CaptureState() => JsonSerializer.SerializeToElement(CaptureGardenState(), s_stateJson);

    /// <summary>Restores a validated garden snapshot. Population state is restored by the host.</summary>
    public void RestoreState(JsonElement state)
    {
        var snapshot = state.Deserialize<GardenState>(s_stateJson)
            ?? throw new InvalidDataException("Garden state is empty.");
        RestoreGardenState(snapshot);
    }

    object ISystemicTransactionalState.CaptureTransactionState() => CaptureGardenState();

    void ISystemicTransactionalState.RestoreTransactionState(object state) =>
        RestoreGardenState((GardenState)state);

    private void CaptureCommandRollback()
    {
        var snapshot = CaptureGardenState();
        _world.TryRegisterRollbackAction(() => RestoreGardenState(snapshot));
    }

    private GardenState CaptureGardenState()
    {
        var indexes = new Dictionary<Reaction, int>(ReferenceEqualityComparer.Instance);
        var reactions = new List<ReactionState>();
        ImpactState CaptureImpact(Impact impact)
        {
            var index = -1;
            if (impact.Reaction is { } reaction && !indexes.TryGetValue(reaction, out index))
            {
                index = reactions.Count;
                indexes.Add(reaction, index);
                reactions.Add(new ReactionState(reaction.CreatureId, reaction.AllowBounce,
                    reaction.Bursts, reaction.Bounced));
            }
            return new ImpactState(impact.Position, index, impact.Depth);
        }

        var intents = _talentIntents.Select(intent => new TalentIntentState(intent.Talent, CaptureImpact(intent.Impact))).ToArray();
        ImpactState? publishing = _publishingImpact is { } impact ? CaptureImpact(impact) : null;
        return new GardenState(1, Tick, BurstCount, RejectedSeedSpawns, _nextCreatureId, _nextMoteId,
            _nextPlantId, Glow, BankedGlow, (int[])_ranks.Clone(), (long[])_spent.Clone(),
            _scent.Capture(), _pending.ToArray(), intents, reactions.ToArray(), publishing);
    }

    private void RestoreGardenState(GardenState snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Tick < 0 || snapshot.BurstCount < 0
            || snapshot.RejectedSeedSpawns < 0 || snapshot.NextCreatureId < 0 || snapshot.NextMoteId < 0
            || snapshot.NextPlantId < 0 || snapshot.Glow < 0 || snapshot.BankedGlow < 0
            || snapshot.Ranks is null || snapshot.Ranks.Length != _ranks.Length
            || snapshot.Ranks.Any(rank => rank is < 0 or > 25)
            || snapshot.Spent is null || snapshot.Spent.Length != _spent.Length || snapshot.Spent.Any(value => value < 0)
            || !_scent.IsValidSnapshot(snapshot.Scent) || snapshot.Pending is null
            || snapshot.Pending.Length > MaxPendingImpacts || snapshot.Intents is null
            || snapshot.Intents.Length > MaxTalentIntents || snapshot.Reactions is null
            || snapshot.Reactions.Length > MaxTalentIntents + 1
            || snapshot.Reactions.Any(reaction => reaction.CreatureId < 0 || reaction.Bursts < 1)
            || snapshot.Pending.Any(pending => !ValidPosition(pending.Position)
                || pending.DueTick < 0 || pending.ExpiryTick < pending.DueTick))
        {
            throw new InvalidDataException("Garden state contains invalid progression, scent, or pending work.");
        }

        var reactions = snapshot.Reactions.Select(reaction => new Reaction(reaction.CreatureId, reaction.AllowBounce)
        {
            Bursts = reaction.Bursts,
            Bounced = reaction.Bounced
        }).ToArray();
        Impact RestoreImpact(ImpactState impact)
        {
            if (!ValidPosition(impact.Position) || impact.Depth < 0
                || impact.ReactionIndex < -1 || impact.ReactionIndex >= reactions.Length)
            {
                throw new InvalidDataException("Garden state contains an invalid impact.");
            }

            return new Impact(impact.Position,
                impact.ReactionIndex == -1 ? null : reactions[impact.ReactionIndex], impact.Depth);
        }

        var intents = snapshot.Intents.Select(intent =>
        {
            if (!Enum.IsDefined(intent.Talent))
            {
                throw new InvalidDataException("Garden state contains an invalid talent.");
            }

            return new TalentIntent(intent.Talent, RestoreImpact(intent.Impact));
        }).ToArray();
        Impact? publishing = snapshot.Publishing is { } impact ? RestoreImpact(impact) : null;

        Tick = snapshot.Tick;
        BurstCount = snapshot.BurstCount;
        RejectedSeedSpawns = snapshot.RejectedSeedSpawns;
        _nextCreatureId = snapshot.NextCreatureId;
        _nextMoteId = snapshot.NextMoteId;
        _nextPlantId = snapshot.NextPlantId;
        Glow = snapshot.Glow;
        BankedGlow = snapshot.BankedGlow;
        snapshot.Ranks.CopyTo(_ranks, 0);
        snapshot.Spent.CopyTo(_spent, 0);
        _scent.Restore(snapshot.Scent);
        _pending.Clear();
        _pending.AddRange(snapshot.Pending);
        _talentIntents.Clear();
        foreach (var intent in intents)
        {
            _talentIntents.Enqueue(intent);
        }

        _publishingImpact = publishing;
    }

    private static bool ValidPosition(Vector2 position) => float.IsFinite(position.X) && float.IsFinite(position.Y)
        && position.X >= 0 && position.X <= Width && position.Y >= 0 && position.Y <= Height;

    private sealed record GardenState(int Version, int Tick, long BurstCount, long RejectedSeedSpawns,
        int NextCreatureId, int NextMoteId, int NextPlantId, long Glow, long BankedGlow,
        int[] Ranks, long[] Spent, float[] Scent, PendingImpact[] Pending,
        TalentIntentState[] Intents, ReactionState[] Reactions, ImpactState? Publishing);
    private readonly record struct ReactionState(int CreatureId, bool AllowBounce, int Bursts, bool Bounced);
    private readonly record struct ImpactState(Vector2 Position, int ReactionIndex, int Depth);
    private readonly record struct TalentIntentState(Talent Talent, ImpactState Impact);
}
