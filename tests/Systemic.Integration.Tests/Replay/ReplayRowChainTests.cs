using System.Buffers.Binary;
using Systemic.Kernel;
using Systemic.Replay;
using Systemic.Rules;

namespace Systemic.Tests.Replay;

/// <summary>
/// A reaction chain that despawns the row it is about must replay identically: the stale row
/// reference resolves to nothing on both runs, so the fresh occupant of the slot is never touched.
/// </summary>
public sealed class ReplayRowChainTests
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void ReplayReproducesARowReactionChainAcrossADespawn()
    {
        var recorder = new ReplayRecorder(new RowChainTarget());
        recorder.RecordTick([new ReplayInput("hit-row", "1")]);
        recorder.RecordTick([]);
        var artifact = recorder.Finish();

        var replayed = new RowChainTarget();
        var result = ReplayRunner.Verify(artifact, replayed);

        Assert.True(result.Success, result.Mismatch?.ToString());
        Assert.Equal(2, replayed.Tick);
        // The chain really did despawn the row it was about, so the assertion below is not vacuous.
        Assert.Equal(1, replayed.Despawns);
        Assert.Equal(4, replayed.Count);
        Assert.Equal([100, 100, 100, 100], replayed.Health);
    }

    private sealed class RowChainTarget : IReplayTarget
    {
        private static readonly PopulationColumnKey<int> s_healthKey =
            new("test.replay-crowd.health", 1, PopulationCodecs.Int32);

        private readonly World _world = new(seed: 7, valueScale: 1);
        private readonly Population<Crowd> _crowd;
        private readonly RuleEngine _rules;

        public RowChainTarget()
        {
            _crowd = _world.DefinePopulation<Crowd>(enableHandles: true);
            var health = _crowd.Column(s_healthKey);
            _crowd.Reserve(4);
            _crowd.AddRange(4);
            _crowd.WithAccess(new PopulationRange(0, 4), health,
                static (PopulationColumn<int> column, scoped ref PopulationAccessScope<Crowd> scope) =>
                    scope.EditRange(column).Fill(100));
            _rules = new RuleEngine(new CompiledRuleSet(
            [
                new CompiledRule("crowd.hit", "crowd.hit", 1, [], [RuleEffect.Emit("crowd.hurt")]),
                new CompiledRule("crowd.hurt", "crowd.hurt", 0, [],
                    [RuleEffect.AdjustProperty(RuleSubject.FactRow(Crowd.Id), "test.replay-crowd.health", -5)]),
            ]));
            _rules.Bind(_world);
        }

        public int Seed => 7;

        public string ContentFingerprint => Fingerprint;

        public int Tick { get; private set; }

        public int Despawns { get; private set; }

        public int Count => _crowd.Count;

        public int[] Health
        {
            get
            {
                var values = new int[_crowd.Count];
                _crowd.WithAccess(new PopulationRange(0, values.Length), (Column: _crowd.Column(s_healthKey), Values: values),
                    static ((PopulationColumn<int> Column, int[] Values) state,
                        scoped ref PopulationAccessScope<Crowd> scope) =>
                    {
                        var health = scope.ReadAll(state.Column);
                        for (var row = 0; row < state.Values.Length; row++)
                        {
                            state.Values[row] = health[row];
                        }
                    });
                return values;
            }
        }

        public void ApplyInput(ReplayInput input)
        {
            var row = int.Parse(input.CanonicalJson, System.Globalization.CultureInfo.InvariantCulture);
            _rules.Dispatch(_world, SimulationFact.ForRow("crowd.hit", _crowd.ReferenceOf(row)), DespawnRowInChain);
        }

        public void Step()
        {
            Tick++;
            _world.LogicalTick = Tick;
        }

        public ReadOnlyMemory<byte> CaptureState()
        {
            var health = Health;
            var bytes = new byte[8 + (health.Length * 4)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, Tick);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), health.Length);
            for (var index = 0; index < health.Length; index++)
            {
                BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8 + (index * 4)), health[index]);
            }

            return bytes;
        }

        private void DespawnRowInChain(SimulationFact fact, SimulationFactEmitter emitter)
        {
            if (fact.Type != "crowd.hurt" || fact.Row is not { } row
                || !_crowd.TryGetHandle(row.Slot, row.Generation, out var handle)
                || !_crowd.TryRemove(handle))
            {
                return;
            }

            Despawns++;
            _crowd.AddRange(1);
            var replacement = _crowd.Count - 1;
            _crowd.WithAccess(new PopulationRange(replacement, 1),
                (Column: _crowd.Column(s_healthKey), Value: 100),
                static ((PopulationColumn<int> Column, int Value) state,
                    scoped ref PopulationAccessScope<Crowd> scope) => scope.EditRange(state.Column)[0] = state.Value);
        }
    }

    private readonly struct Crowd : IPopulationKind
    {
        public static string Id => "test.replay-crowd";

        public static int Version => 1;
    }
}
