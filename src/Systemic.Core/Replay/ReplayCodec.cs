using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Systemic.Replay;

/// <summary>Reads and writes the versioned, bounded replay artifact.</summary>
public static class ReplayCodec
{
    private const int MaximumArtifactBytes = 64 * 1024 * 1024;

    public static string Serialize(ReplayArtifact artifact)
    {
        Validate(artifact);
        using var stream = new BoundedMemoryStream(MaximumArtifactBytes);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version"u8, artifact.Version);
            writer.WriteNumber("seed"u8, artifact.Seed);
            writer.WriteString("contentFingerprint"u8, artifact.ContentFingerprint);
            writer.WriteNumber("initialTick"u8, artifact.InitialTick);
            writer.WriteString("initialStateHash"u8, artifact.InitialStateHash);
            writer.WriteStartArray("ticks"u8);
            foreach (var tick in artifact.Ticks)
            {
                writer.WriteStartObject();
                writer.WriteNumber("tick"u8, tick.Tick);
                writer.WriteStartArray("inputs"u8);
                foreach (var input in tick.Inputs)
                {
                    writer.WriteStartObject();
                    writer.WriteString("type"u8, input.Type);
                    writer.WritePropertyName("value"u8);
                    writer.WriteRawValue(input.CanonicalJson);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteString("stateHash"u8, tick.StateHash);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static ReplayArtifact Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > MaximumArtifactBytes || Encoding.UTF8.GetByteCount(json) > MaximumArtifactBytes)
            throw new ArgumentException("Replay exceeds the 64 MiB UTF-8 limit.", nameof(json));
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            RequireFields(root, "version", "seed", "contentFingerprint", "initialTick", "initialStateHash", "ticks");
            var ticks = ImmutableArray.CreateBuilder<ReplayTick>();
            foreach (var tick in root.GetProperty("ticks"u8).EnumerateArray())
            {
                RequireFields(tick, "tick", "inputs", "stateHash");
                var inputs = ImmutableArray.CreateBuilder<ReplayInput>();
                foreach (var input in tick.GetProperty("inputs"u8).EnumerateArray())
                {
                    RequireFields(input, "type", "value");
                    inputs.Add(new ReplayInput(input.GetProperty("type"u8).GetString()!, input.GetProperty("value"u8).GetRawText()));
                }
                ticks.Add(new ReplayTick(tick.GetProperty("tick"u8).GetInt32(), inputs.ToImmutable(),
                    tick.GetProperty("stateHash"u8).GetString()!));
            }
            var result = new ReplayArtifact(root.GetProperty("version"u8).GetInt32(), root.GetProperty("seed"u8).GetInt32(),
                root.GetProperty("contentFingerprint"u8).GetString()!, root.GetProperty("initialTick"u8).GetInt32(),
                root.GetProperty("initialStateHash"u8).GetString()!, ticks.ToImmutable());
            Validate(result);
            return result;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException
            or KeyNotFoundException or OverflowException)
        {
            throw new ArgumentException("Replay artifact is malformed.", nameof(json), exception);
        }
    }

    internal static void Validate(ReplayArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Version != 1 || artifact.InitialTick < 0 || !ReplayStateHash.Valid(artifact.ContentFingerprint)
            || !ReplayStateHash.Valid(artifact.InitialStateHash) || artifact.Ticks.IsDefault || artifact.Ticks.Length > 1_000_000)
            throw new ArgumentException("Replay metadata is invalid.", nameof(artifact));
        var tick = artifact.InitialTick;
        foreach (var item in artifact.Ticks)
        {
            if (item is null || tick == int.MaxValue || item.Tick != ++tick || !ReplayStateHash.Valid(item.StateHash)
                || item.Inputs.IsDefault || item.Inputs.Length > 65_536 || item.Inputs.Any(input => input is null))
                throw new ArgumentException("Replay ticks must be contiguous and contain valid bounded inputs and hashes.", nameof(artifact));
        }
    }

    private static void RequireFields(JsonElement element, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new ArgumentException("Replay field must be an object.");
        var allowed = new HashSet<string>(fields, StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name) || !found.Add(property.Name))
                throw new ArgumentException($"Unknown or duplicate replay property '{property.Name}'.");
        if (found.Count != allowed.Count) throw new ArgumentException("Replay object is missing a required property.");
    }

    private sealed class BoundedMemoryStream(long maximumLength) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacityFor(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacityFor(buffer.Length);
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            EnsureCapacityFor(1);
            base.WriteByte(value);
        }

        private void EnsureCapacityFor(int count)
        {
            if (count > maximumLength - Length)
                throw new ArgumentException("Replay exceeds the 64 MiB UTF-8 limit.");
        }
    }
}
