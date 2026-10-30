using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Systemic.Tests.Save;

// Seal deliberately malformed fixtures so tests reach schema and semantic validation.
internal static class SaveTestDocument
{
    public static string Seal(JsonObject root)
    {
        using var document = JsonDocument.Parse(root.ToJsonString());
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            Write(writer, document.RootElement, root: true);
        }
        root["checksum"] = Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
        return root.ToJsonString();
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element, bool root = false)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                if (root && property.Name == "checksum")
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in element.EnumerateArray())
            {
                Write(writer, child);
            }

            writer.WriteEndArray();
        }
        else
        {
            element.WriteTo(writer);
        }
    }
}
