using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using Systemic.Content;

namespace Systemic.Save;

/// <summary>Applies explicit, sequential migrations to an intact v4-or-later save.</summary>
public static class SystemicSaveMigration
{
    /// <summary>
    /// Verifies the original checksum, upgrades a private copy, and seals the result.
    /// The caller must validate the resulting target-version schema before using it.
    /// </summary>
    public static string Migrate(string json, int targetVersion, IEnumerable<ISaveMigration> migrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ArgumentNullException.ThrowIfNull(migrations);
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > ContentFileReader.MaximumFileBytes)
            {
                throw new SystemicSaveException("Save document exceeds the 64 MiB limit.");
            }
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.ValueKind != JsonValueKind.Object
                || !parsed.RootElement.TryGetProperty("schemaVersion"u8, out var versionNode)
                || !versionNode.TryGetInt32(out var version) || version < 4)
            {
                throw new SystemicSaveException("Save migration requires a version 4 or later document.");
            }
            if (targetVersion < version)
            {
                throw new SystemicSaveException("Save migration cannot downgrade a document.");
            }
            SaveIntegrity.Validate(parsed.RootElement);
            var steps = new Dictionary<int, ISaveMigration>();
            foreach (var migration in migrations)
            {
                ArgumentNullException.ThrowIfNull(migration);
                if (migration.FromVersion < 4 || !steps.TryAdd(migration.FromVersion, migration))
                {
                    throw new ArgumentException($"Save migration from version {migration.FromVersion} is invalid or duplicated.", nameof(migrations));
                }
            }

            var document = JsonNode.Parse(json)!.AsObject();
            while (version < targetVersion)
            {
                if (!steps.TryGetValue(version, out var migration))
                {
                    throw new SystemicSaveException($"No save migration is registered from version {version} to {version + 1}.");
                }
                migration.Apply(document);
                document["schemaVersion"] = ++version;
            }
            return SaveIntegrity.Seal(document);
        }
        catch (SystemicSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            throw new SystemicSaveException($"Could not migrate save: {exception.Message}", exception);
        }
    }
}
