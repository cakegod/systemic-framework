using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Systemic.Save;

// Canonical JSON sorts every object's keys ordinally, retains array order and number
// spelling, and emits UTF-8 without whitespace. The root checksum is excluded from
// its own digest. Duplicate keys are rejected rather than interpreted ambiguously.
internal static class SaveIntegrity
{
    public static bool IsDigest(string? digest) => digest is { Length: 64 }
        && digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static void Validate(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object
            || !document.TryGetProperty("checksum"u8, out var checksumNode)
            || checksumNode.ValueKind != JsonValueKind.String
            || !IsDigest(checksumNode.GetString()))
        {
            throw new SystemicSaveException("Save requires a lowercase SHA-256 checksum.");
        }

        var expected = Convert.FromHexString(checksumNode.GetString()!);
        var actual = SHA256.HashData(CanonicalBytes(document, excludeChecksum: true));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
        {
            throw new SystemicSaveException("Save checksum does not match its contents.");
        }
    }

    public static string Seal(JsonObject document)
    {
        using var unsealed = JsonDocument.Parse(document.ToJsonString());
        document["checksum"] = Convert.ToHexStringLower(SHA256.HashData(
            CanonicalBytes(unsealed.RootElement, excludeChecksum: true)));
        using var sealedDocument = JsonDocument.Parse(document.ToJsonString());
        return Encoding.UTF8.GetString(CanonicalBytes(sealedDocument.RootElement, excludeChecksum: false));
    }

    private static byte[] CanonicalBytes(JsonElement element, bool excludeChecksum)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteValue(writer, element, excludeChecksum, isRoot: true);
        }
        return stream.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement element, bool excludeChecksum, bool isRoot)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                string? previous = null;
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (string.Equals(previous, property.Name, StringComparison.Ordinal))
                    {
                        throw new SystemicSaveException($"Save contains duplicate JSON property '{property.Name}'.");
                    }
                    previous = property.Name;
                    if (isRoot && excludeChecksum && property.NameEquals("checksum"u8))
                    {
                        continue;
                    }
                    writer.WritePropertyName(property.Name);
                    WriteValue(writer, property.Value, excludeChecksum, isRoot: false);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteValue(writer, item, excludeChecksum, isRoot: false);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
