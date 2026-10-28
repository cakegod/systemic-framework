using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Replay;
using Systemic.Rules;

namespace Systemic.Tests.Replay;

public sealed class DeterministicReplayTests
{
    private const string Fingerprint = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void State_hash_uses_versioned_length_framing_and_sha256()
    {
        Assert.Equal("f5aad88cfa6639367771eb2fff79c43d670781ba4d1e876c611fb8a85ecea619",
            ReplayStateHash.Compute(Encoding.UTF8.GetBytes("abc")));
        Assert.NotEqual(ReplayStateHash.Compute("a"u8), ReplayStateHash.Compute("abc"u8));
    }

    [Fact]
    public void Inputs_canonicalize_keys_and_numeric_spelling_while_preserving_array_order_and_negative_zero()
    {
        var first = new ReplayInput("move", "{\"z\":[2.00,1e0],\"a\":{\"y\":-0.0,\"x\":123e-2}}");
        var second = new ReplayInput("move", "{\"a\":{\"x\":1.230,\"y\":-0},\"z\":[2,1]}");
        Assert.Equal("{\"a\":{\"x\":1.23,\"y\":-0},\"z\":[2,1]}", first.CanonicalJson);
        Assert.Equal(first.CanonicalJson, second.CanonicalJson);
        Assert.NotEqual(first.CanonicalJson, new ReplayInput("move", "{\"a\":{\"x\":1.23,\"y\":0},\"z\":[1,2]}").CanonicalJson);
        Assert.Throws<ArgumentException>(() => new ReplayInput("move", "{\"x\":1,\"x\":2}"));
        Assert.Throws<ArgumentException>(() => new ReplayInput("move", "{\"outer\":{\"x\":1,\"\\u0078\":2}}"));
        Assert.Throws<ArgumentException>(() => new ReplayInput("move", "[NaN]"));
        Assert.Throws<ArgumentException>(() => new ReplayInput("", "null"));
        Assert.Throws<ArgumentException>(() => new ReplayInput("move", "1e1000001"));
    }

    [Fact]
    public void Recorder_round_trips_inputs_and_replays_each_tick_in_original_order()
    {
        var recorder = new ReplayRecorder(new CounterTarget(7, Fingerprint));
        recorder.RecordTick([new ReplayInput("add", "2.0"), new ReplayInput("multiply", "3")]);
        recorder.RecordTick([]);
        recorder.RecordTick([new ReplayInput("add", "-4")]);
        var artifact = recorder.Finish();
        var json = ReplayCodec.Serialize(artifact);
        var restored = ReplayCodec.Deserialize(json);
        Assert.Equal(json, ReplayCodec.Serialize(restored));
        Assert.Equal(7, restored.Seed);
        Assert.Equal(Fingerprint, restored.ContentFingerprint);
        Assert.Equal([1, 2, 3], restored.Ticks.Select(tick => tick.Tick));
        Assert.Equal(["add", "multiply"], restored.Ticks[0].Inputs.Select(input => input.Type));
        var target = new CounterTarget(7, Fingerprint);
        var result = ReplayRunner.Verify(restored, target);
        Assert.True(result.Success, result.Mismatch?.ToString());
        Assert.Equal(23, target.Value);
        Assert.Null(ReplayDiff.Compare(artifact, restored));
    }

    [Fact]
    public void Runner_reports_the_first_divergent_tick_and_never_applies_later_inputs()
    {
        var recorder = new ReplayRecorder(new CounterTarget(3, Fingerprint));
        recorder.RecordTick([new ReplayInput("add", "2")]);
        recorder.RecordTick([new ReplayInput("add", "4")]);
        recorder.RecordTick([new ReplayInput("add", "8")]);
        var artifact = recorder.Finish();
        var target = new CounterTarget(3, Fingerprint) { DriftAtTick = 2 };
        var result = ReplayRunner.Verify(artifact, target);
        Assert.False(result.Success);
        Assert.Equal("state", result.Mismatch!.Code);
        Assert.Equal(2, result.Mismatch.Tick);
        Assert.Equal(artifact.Ticks[1].StateHash, result.Mismatch.Expected);
        Assert.NotEqual(result.Mismatch.Expected, result.Mismatch.Actual);
        Assert.Equal(2, target.Tick);
        Assert.Equal(2, target.InputCount);
    }

    [Fact]
    public void Runner_rejects_seed_fingerprint_and_initial_state_mismatches_before_applying_inputs()
    {
        var recorder = new ReplayRecorder(new CounterTarget(4, Fingerprint));
        recorder.RecordTick([new ReplayInput("add", "2")]);
        var artifact = recorder.Finish();
        var wrongSeed = new CounterTarget(5, Fingerprint);
        Assert.Equal("seed", ReplayRunner.Verify(artifact, wrongSeed).Mismatch!.Code);
        Assert.Equal(0, wrongSeed.InputCount);
        var wrongContent = new CounterTarget(4, new string('f', 64));
        Assert.Equal("contentFingerprint", ReplayRunner.Verify(artifact, wrongContent).Mismatch!.Code);
        var wrongState = new CounterTarget(4, Fingerprint) { Value = 99 };
        Assert.Equal("initialState", ReplayRunner.Verify(artifact, wrongState).Mismatch!.Code);
        Assert.Equal(0, wrongState.InputCount);
    }

    [Fact]
    public void Diff_reports_input_order_and_hash_divergence_with_tick_and_input_index()
    {
        var first = new ReplayRecorder(new CounterTarget(2, Fingerprint));
        first.RecordTick([new ReplayInput("add", "3"), new ReplayInput("multiply", "2")]);
        var second = new ReplayRecorder(new CounterTarget(2, Fingerprint));
        second.RecordTick([new ReplayInput("multiply", "2"), new ReplayInput("add", "3")]);
        var difference = ReplayDiff.Compare(first.Finish(), second.Finish());
        Assert.NotNull(difference);
        Assert.Equal("input", difference!.Code);
        Assert.Equal(1, difference.Tick);
        Assert.Equal(0, difference.InputIndex);
    }

    [Fact]
    public void Recorder_rejects_unrecorded_state_changes_and_failed_ticks_cannot_be_finished()
    {
        var target = new CounterTarget(1, Fingerprint);
        var recorder = new ReplayRecorder(target);
        target.Value = 10;
        Assert.Throws<InvalidOperationException>(() => recorder.RecordTick([]));
        Assert.Throws<InvalidOperationException>(() => recorder.Finish());
        var failing = new ReplayRecorder(new CounterTarget(1, Fingerprint));
        Assert.Throws<InvalidOperationException>(() => failing.RecordTick([new ReplayInput("invalid", "0")]));
        Assert.Throws<InvalidOperationException>(() => failing.Finish());
    }

    [Fact]
    public void Artifact_parser_rejects_duplicates_versions_gaps_and_malformed_hashes()
    {
        var recorder = new ReplayRecorder(new CounterTarget(9, Fingerprint));
        recorder.RecordTick([]);
        var json = ReplayCodec.Serialize(recorder.Finish());
        Assert.Throws<ArgumentException>(() => ReplayCodec.Deserialize(json.Replace("\"version\":1", "\"version\":2")));
        Assert.Throws<ArgumentException>(() => ReplayCodec.Deserialize(json.Replace("\"tick\":1", "\"tick\":2")));
        Assert.Throws<ArgumentException>(() => ReplayCodec.Deserialize(json.Replace(Fingerprint, "short")));
        Assert.Throws<ArgumentException>(() => ReplayCodec.Deserialize(json.Replace("\"seed\":9", "\"seed\":9,\"seed\":9")));
        Assert.Throws<ArgumentException>(() => ReplayCodec.Deserialize(json.Replace("\"seed\":9", "\"seed\":9,\"unknown\":true")));
        Assert.Throws<ArgumentException>(() => ReplayCodec.Deserialize("{}"));
    }

    [Fact]
    public void Replay_serializer_rejects_artifacts_over_the_utf8_byte_limit()
    {
        var input = new ReplayInput("payload", "\"" + new string('é', 1_000_000) + "\"");
        var tick = new ReplayTick(1, Enumerable.Repeat(input, 34).ToImmutableArray(), Fingerprint);
        var artifact = new ReplayArtifact(1, 7, Fingerprint, 0, Fingerprint, [tick]);

        Assert.Throws<ArgumentException>(() => ReplayCodec.Serialize(artifact));
    }

    [Fact]
    public void Replay_parser_counts_utf8_bytes_instead_of_utf16_characters()
    {
        var largeType = new string('é', 33_554_433);
        var json = string.Concat(
            "{\"version\":1,\"seed\":7,\"contentFingerprint\":\"", Fingerprint,
            "\",\"initialTick\":0,\"initialStateHash\":\"", Fingerprint,
            "\",\"ticks\":[{\"tick\":1,\"inputs\":[{\"type\":\"", largeType,
            "\",\"value\":0}],\"stateHash\":\"", Fingerprint, "\"}]}");

        Assert.True(json.Length < 64 * 1024 * 1024);
        Assert.Throws<ArgumentException>(() => ReplayCodec.Deserialize(json));
    }

    [Fact]
    public void Host_adapter_excludes_declared_transient_extensions_and_the_save_checksum()
    {
        var left = CreateHost(17, transient: 1);
        var right = CreateHost(17, transient: 999);
        var leftTarget = new HostReplayTarget(left.Host, (game, input) => { }, ["test.transient"]);
        var rightTarget = new HostReplayTarget(right.Host, (game, input) => { }, ["test.transient"]);
        Assert.Equal(ReplayStateHash.Compute(leftTarget.CaptureState().Span), ReplayStateHash.Compute(rightTarget.CaptureState().Span));
        var includeTransient = new HostReplayTarget(right.Host, (game, input) => { });
        Assert.NotEqual(ReplayStateHash.Compute(leftTarget.CaptureState().Span), ReplayStateHash.Compute(includeTransient.CaptureState().Span));
    }

    [Fact]
    public void Host_fixture_matches_checked_in_replay_and_reports_mismatches()
    {
        var setup = CreateHost(seed: 41, transient: 7);
        var target = new HostReplayTarget(setup.Host, (game, input) =>
        {
            using var payload = JsonDocument.Parse(input.CanonicalJson);
            setup.System.Value += payload.RootElement.GetInt32();
        }, ["test.transient"]);
        var expected = ReplayCodec.Deserialize(HostReplayFixture.Json);
        var result = ReplayRunner.Verify(expected, target);
        Assert.True(result.Success, result.Mismatch?.ToString());
        Assert.Equal(3, setup.Host.Game.World.LogicalTick);
    }

    private static (HeadlessSystemicHost Host, CounterSystem System) CreateHost(int seed, int transient)
    {
        var content = new ContentPack("", new GameManifest("replay-test", "Replay test", 30, 4),
            new CompiledRuleSet([]), new CompiledEntities(ImmutableArray<EntityDefinition>.Empty))
        {
            ContentFingerprint = Fingerprint
        };
        var system = new CounterSystem();
        var host = HeadlessSystemicHost.Create(content, seed, configure: builder =>
        {
            builder.AddSystem(system);
            builder.RegisterStatefulExtension(new TransientExtension(transient));
        });
        return (host, system);
    }

    private sealed class CounterTarget(int seed, string fingerprint) : IReplayTarget
    {
        public int Seed => seed;
        public string ContentFingerprint => fingerprint;
        public int Tick { get; private set; }
        public int Value { get; set; }
        public int DriftAtTick { get; init; } = -1;
        public int InputCount { get; private set; }
        public void ApplyInput(ReplayInput input)
        {
            InputCount++;
            var value = int.Parse(input.CanonicalJson, CultureInfo.InvariantCulture);
            Value = input.Type switch
            {
                "add" => checked(Value + value),
                "multiply" => checked(Value * value),
                _ => throw new InvalidOperationException("Unknown input.")
            };
        }
        public void Step()
        {
            Tick++;
            Value += seed;
            if (Tick == DriftAtTick) Value++;
        }
        public ReadOnlyMemory<byte> CaptureState()
        {
            var bytes = new byte[8];
            BinaryPrimitives.WriteInt32BigEndian(bytes, Tick);
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), Value);
            return bytes;
        }
    }

    private sealed class CounterSystem : ISystemicSystem, ISystemicStatefulExtension
    {
        public string Id => "test.counter";
        public int Order => 0;
        public int Value { get; set; }
        public void Step(SystemicSystemContext context) =>
            Value = (Value * 7 + context.Tick + (BitConverter.SingleToInt32Bits(context.GetWritableWorld().Draws.NextFloat()) & 0xffff)) % 1_000_000;
        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(Value);
        public void RestoreState(JsonElement state) => Value = state.GetInt32();
    }

    private sealed class TransientExtension(int value) : ISystemicStatefulExtension
    {
        public string Id => "test.transient";
        public JsonElement CaptureState() => JsonSerializer.SerializeToElement(value);
        public void RestoreState(JsonElement state) { }
    }
}

internal static class HostReplayFixture
{
    public const string Json = """{"version":1,"seed":41,"contentFingerprint":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","initialTick":0,"initialStateHash":"6dd2fbc04d0e2ad90ef92a3cd8fd2767766678e39784c697243851cd3505846d","ticks":[{"tick":1,"inputs":[{"type":"add","value":2}],"stateHash":"b0b4804e688f126ea8af048fde1f6bf267da14b6b7342e85ace1262451a45c96"},{"tick":2,"inputs":[],"stateHash":"41b1d56dcbbf2e00d11dd838bbe65d04209b7f2a55a352e7abf71d7ec4cf0a0a"},{"tick":3,"inputs":[{"type":"add","value":1}],"stateHash":"92ba7af4d79069329be67c84ad07507c23042f2b06fc3c0de7332da2f6f53941"}]}""";
}
