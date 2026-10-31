using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Systemic.Content;
using Systemic.Host;

namespace Systemic.Replay;

public sealed record ReplayInput
{
    public ReplayInput(string type, string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(json);
        Type = type;
        CanonicalJson = CanonicalReplayJson.Normalize(json);
    }
    public string Type { get; }
    public string CanonicalJson { get; }
}

public interface IReplayTarget
{
    int Seed { get; }
    string ContentFingerprint { get; }
    int Tick { get; }
    void ApplyInput(ReplayInput input);
    void Step();
    ReadOnlyMemory<byte> CaptureState();

    /// <summary>Gets the module versions that produced this target's state.</summary>
    /// <remarks>The default is empty so simple targets stay valid; a host target reports every module.</remarks>
    IReadOnlyDictionary<string, int> ModuleVersions => ImmutableDictionary<string, int>.Empty;
}

public sealed record ReplayTick(int Tick, ImmutableArray<ReplayInput> Inputs, string StateHash);
public sealed record ReplayArtifact(int Version, int Seed, string ContentFingerprint, int InitialTick,
    string InitialStateHash, ImmutableArray<ReplayTick> Ticks)
{
    /// <summary>Gets the module versions that produced this replay.</summary>
    public ImmutableDictionary<string, int> ModuleVersions { get; init; } =
        ImmutableDictionary<string, int>.Empty;
}
public sealed record ReplayMismatch(string Code, int Tick, string? Expected, string? Actual, int? InputIndex = null);
public sealed record ReplayVerificationResult(bool Success, ReplayMismatch? Mismatch);

public static class ReplayStateHash
{
    public static string Compute(ReadOnlySpan<byte> state)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Systemic.Replay.State.v1\0"u8);
        Span<byte> size = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(size, (ulong)state.Length);
        hash.AppendData(size);
        hash.AppendData(state);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal static bool Valid(string? hash) => hash is { Length: 64 }
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

public sealed class ReplayRecorder
{
    private readonly IReplayTarget _target;
    private readonly int _seed, _initialTick;
    private readonly string _fingerprint, _initialHash;
    private readonly List<ReplayTick> _ticks = [];
    private readonly ImmutableDictionary<string, int> _moduleVersions;
    private string _lastHash;
    private bool _failed;

    public ReplayRecorder(IReplayTarget target)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        if (!ReplayStateHash.Valid(target.ContentFingerprint) || target.Tick < 0)
        {
            throw new ArgumentException("Replay target requires a valid content fingerprint and non-negative tick.", nameof(target));
        }

        _seed = target.Seed;
        _fingerprint = target.ContentFingerprint;
        _initialTick = target.Tick;
        _moduleVersions = target.ModuleVersions.ToImmutableDictionary(StringComparer.Ordinal);
        _lastHash = _initialHash = ReplayStateHash.Compute(target.CaptureState().Span);
    }

    public void RecordTick(IEnumerable<ReplayInput> inputs)
    {
        if (_failed)
        {
            throw new InvalidOperationException("This recording has failed and cannot continue.");
        }

        try
        {
            ArgumentNullException.ThrowIfNull(inputs);
            if (_target.Seed != _seed || _target.ContentFingerprint != _fingerprint
                || _target.Tick != _initialTick + _ticks.Count
                || ReplayStateHash.Compute(_target.CaptureState().Span) != _lastHash)
            {
                throw new InvalidOperationException("Replay target changed outside the recorded inputs and ticks.");
            }

            var copy = inputs.ToImmutableArray();
            foreach (var input in copy)
            {
                _target.ApplyInput(input ?? throw new ArgumentException("Replay input is null."));
            }

            _target.Step();
            var expectedTick = checked(_initialTick + _ticks.Count + 1);
            if (_target.Tick != expectedTick)
            {
                throw new InvalidOperationException("Replay target did not advance exactly one tick.");
            }

            _lastHash = ReplayStateHash.Compute(_target.CaptureState().Span);
            _ticks.Add(new ReplayTick(expectedTick, copy, _lastHash));
        }
        catch { _failed = true; throw; }
    }

    public ReplayArtifact Finish()
    {
        if (_failed || _target.Tick != _initialTick + _ticks.Count
            || ReplayStateHash.Compute(_target.CaptureState().Span) != _lastHash)
        {
            throw new InvalidOperationException("Recording cannot finish after a failed tick or external state change.");
        }

        return new ReplayArtifact(1, _seed, _fingerprint, _initialTick, _initialHash, [.. _ticks])
        {
            ModuleVersions = _moduleVersions,
        };
    }
}

public static class ReplayRunner
{
    public static ReplayVerificationResult Verify(ReplayArtifact artifact, IReplayTarget target)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(target);
        ReplayCodec.Validate(artifact);
        ReplayVerificationResult Fail(string code, int tick, string? expected, string? actual) =>
            new(false, new ReplayMismatch(code, tick, expected, actual));
        if (artifact.Seed != target.Seed)
        {
            return Fail("seed", target.Tick, artifact.Seed.ToString(), target.Seed.ToString());
        }

        if (artifact.ContentFingerprint != target.ContentFingerprint)
        {
            return Fail("contentFingerprint", target.Tick, artifact.ContentFingerprint, target.ContentFingerprint);
        }

        if (artifact.InitialTick != target.Tick)
        {
            return Fail("initialTick", target.Tick, artifact.InitialTick.ToString(), target.Tick.ToString());
        }

        foreach (var (moduleId, version) in artifact.ModuleVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!target.ModuleVersions.TryGetValue(moduleId, out var actual))
            {
                return Fail("moduleVersion", target.Tick, $"{moduleId}:{version}", "missing");
            }

            if (actual != version)
            {
                return Fail("moduleVersion", target.Tick, $"{moduleId}:{version}", $"{moduleId}:{actual}");
            }
        }

        foreach (var (moduleId, version) in target.ModuleVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!artifact.ModuleVersions.ContainsKey(moduleId))
            {
                return Fail("moduleVersion", target.Tick, "missing", $"{moduleId}:{version}");
            }
        }

        var initial = ReplayStateHash.Compute(target.CaptureState().Span);
        if (artifact.InitialStateHash != initial)
        {
            return Fail("initialState", target.Tick, artifact.InitialStateHash, initial);
        }

        foreach (var tick in artifact.Ticks)
        {
            foreach (var input in tick.Inputs)
            {
                target.ApplyInput(input);
            }

            target.Step();
            if (target.Tick != tick.Tick)
            {
                return Fail("tick", tick.Tick, tick.Tick.ToString(), target.Tick.ToString());
            }

            var actual = ReplayStateHash.Compute(target.CaptureState().Span);
            if (actual != tick.StateHash)
            {
                return Fail("state", tick.Tick, tick.StateHash, actual);
            }
        }
        return new(true, null);
    }
}

public static class ReplayDiff
{
    public static ReplayMismatch? Compare(ReplayArtifact first, ReplayArtifact second)
    {
        ReplayCodec.Validate(first);
        ReplayCodec.Validate(second);
        if (first.Seed != second.Seed)
        {
            return new("seed", first.InitialTick, first.Seed.ToString(), second.Seed.ToString());
        }

        if (first.ContentFingerprint != second.ContentFingerprint)
        {
            return new("contentFingerprint", first.InitialTick, first.ContentFingerprint, second.ContentFingerprint);
        }

        if (first.InitialTick != second.InitialTick)
        {
            return new("initialTick", first.InitialTick, first.InitialTick.ToString(), second.InitialTick.ToString());
        }

        foreach (var (moduleId, version) in first.ModuleVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!second.ModuleVersions.TryGetValue(moduleId, out var other))
            {
                return new("moduleVersion", first.InitialTick, $"{moduleId}:{version}", "missing");
            }

            if (other != version)
            {
                return new("moduleVersion", first.InitialTick, $"{moduleId}:{version}", $"{moduleId}:{other}");
            }
        }

        foreach (var (moduleId, version) in second.ModuleVersions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (!first.ModuleVersions.ContainsKey(moduleId))
            {
                return new("moduleVersion", first.InitialTick, "missing", $"{moduleId}:{version}");
            }
        }

        if (first.InitialStateHash != second.InitialStateHash)
        {
            return new("initialState", first.InitialTick, first.InitialStateHash, second.InitialStateHash);
        }

        for (var i = 0; i < Math.Min(first.Ticks.Length, second.Ticks.Length); i++)
        {
            var a = first.Ticks[i]; var b = second.Ticks[i];
            for (var j = 0; j < Math.Max(a.Inputs.Length, b.Inputs.Length); j++)
            {
                var left = j < a.Inputs.Length ? a.Inputs[j] : null;
                var right = j < b.Inputs.Length ? b.Inputs[j] : null;
                if (left != right)
                {
                    return new("input", a.Tick, left?.ToString(), right?.ToString(), j);
                }
            }
            if (a.StateHash != b.StateHash)
            {
                return new("state", a.Tick, a.StateHash, b.StateHash);
            }
        }
        return first.Ticks.Length == second.Ticks.Length ? null
            : new("length", first.InitialTick, first.Ticks.Length.ToString(), second.Ticks.Length.ToString());
    }
}

/// <summary>Hashes host save state, excluding its checksum and explicitly transient extensions.</summary>
public sealed class HostReplayTarget(ISystemicHostAdapter host, Action<SystemicGame, ReplayInput> applyInput,
    IEnumerable<string>? transientExtensions = null) : IReplayTarget
{
    private readonly HashSet<string> _transient = new(transientExtensions ?? [], StringComparer.Ordinal);
    public int Seed => host.Game.World.Seed;
    public string ContentFingerprint => host.Game.Content.ContentFingerprint
        ?? throw new InvalidOperationException("Replay content has no fingerprint.");
    public int Tick => host.Game.World.LogicalTick;
    public void ApplyInput(ReplayInput input) => applyInput(host.Game, input);
    public void Step() => host.Step();
    public IReadOnlyDictionary<string, int> ModuleVersions
    {
        get
        {
            var versions = host.Game.ModuleVersions.ToBuilder();
            if (!host.Game.Content.Modules.IsDefaultOrEmpty)
            {
                foreach (var module in host.Game.Content.Modules)
                {
                    versions[module.Module.Id] = module.Module.Version;
                }
            }

            return versions.ToImmutable();
        }
    }
    public ReadOnlyMemory<byte> CaptureState()
    {
        var document = JsonNode.Parse(host.Save())!.AsObject();
        document.Remove("checksum");
        if (document["extensionStates"] is JsonObject extensions)
        {
            foreach (var name in _transient)
            {
                extensions.Remove(name);
            }
        }
        // WHY: host state is bounded by the save limit, not the smaller limit for authored replay inputs.
        return Encoding.UTF8.GetBytes(CanonicalReplayJson.Normalize(document.ToJsonString(),
            checked((int)ContentFileReader.MaximumFileBytes)));
    }
}
