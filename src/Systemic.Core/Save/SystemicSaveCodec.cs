using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Save;

public interface IEntityStateCodec
{
    string Id { get; }
    bool CanEncode(Entity entity);
    JsonElement Encode(Entity entity);
    Entity Decode(JsonElement payload);
}

public interface ISaveMigration
{
    int FromVersion { get; }
    void Apply(JsonObject document);
}

public sealed class SystemicSaveException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>Captures and restores portable engine-neutral world state using explicit entity codecs.</summary>
public sealed class SystemicSaveCodec
{
    public const int CurrentSchemaVersion = 6;
    private const string SaveSchemaId = "https://press-the-button.dev/systemic/save.schema.json";
    private static readonly string s_saveSchema = ReadSchema();

    private readonly Dictionary<string, IEntityStateCodec> _codecs;
    private readonly Dictionary<int, ISaveMigration> _migrations;

    public SystemicSaveCodec(
        IEnumerable<IEntityStateCodec>? entityCodecs = null,
        IEnumerable<ISaveMigration>? migrations = null)
    {
        _codecs = new Dictionary<string, IEntityStateCodec>(StringComparer.Ordinal)
        {
            [DataEntityStateCodec.CodecId] = new DataEntityStateCodec()
        };
        foreach (var codec in entityCodecs ?? [])
        {
            ArgumentNullException.ThrowIfNull(codec);
            ArgumentException.ThrowIfNullOrWhiteSpace(codec.Id);
            if (!_codecs.TryAdd(codec.Id, codec))
            {
                throw new ArgumentException($"Entity save codec '{codec.Id}' is registered more than once.", nameof(entityCodecs));
            }
        }

        // Version 6 retired the typed event engine, so its counters are dropped on load by default.
        // A caller-supplied migration replaces that default for the same version.
        _migrations = new Dictionary<int, ISaveMigration>
        {
            [5] = new RetiredEventEngineStateMigration()
        };
        var provided = new HashSet<int>();
        foreach (var migration in migrations ?? [])
        {
            ArgumentNullException.ThrowIfNull(migration);
            if (migration.FromVersion < 4 || !provided.Add(migration.FromVersion))
            {
                throw new ArgumentException($"Save migration from version {migration.FromVersion} is invalid or duplicated.", nameof(migrations));
            }

            _migrations[migration.FromVersion] = migration;
        }
    }

    public string Save(SystemicGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var contentFingerprint = RequireContentFingerprint(game.Content);
        var world = game.World;
        var entities = world.Entities.OrderBy(entity => entity.Id)
            .Select(entity => CaptureEntity(world, entity))
            .ToArray();
        var extensionStates = game.StatefulExtensions.ToDictionary(extension => extension.Id,
            extension => CloneJson(extension.CaptureState()), StringComparer.Ordinal);
        if (world.HasPopulations && !game.StatefulExtensions.Any(extension => extension is PopulationStatefulExtension))
        {
            var populationState = new PopulationStatefulExtension(world).CaptureState();
            if (!extensionStates.TryAdd(PopulationStatefulExtension.ExtensionId, populationState))
            {
                throw new SystemicSaveException($"Stateful extension ID '{PopulationStatefulExtension.ExtensionId}' is reserved for populations.");
            }
        }

        var document = new SaveDocument(
            CurrentSchemaVersion,
            contentFingerprint,
            "",
            new SavedWorld(
                world.Seed,
                world.ValueScale,
                world.SpatialCellSize,
                world.LogicalTick,
                world.NextEntityId,
                world.Draws.CaptureState(),
                entities,
                [.. world.Modifiers],
                [.. world.Grants],
                world.BoundRoles.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                [.. world.RequiredEntityIds]),
            // A scope reset can remove a named entity, so only names that still resolve are written.
            // A stale name would fail this document's own load-time reference check.
            game.EntityIds
                .Where(pair => world.ById(pair.Value) is { ToRemove: false })
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            extensionStates);

        var json = JsonSerializer.Serialize(document, SystemicSaveJsonContext.Default.SaveDocument);
        var sealedJson = SaveIntegrity.Seal(JsonNode.Parse(json)!.AsObject());
        if (Encoding.UTF8.GetByteCount(sealedJson) > ContentFileReader.MaximumFileBytes)
        {
            throw new SystemicSaveException("Save document exceeds the 64 MiB limit.");
        }
        return sealedJson;
    }

    /// <summary>Writes and flushes a temporary file beside the destination, then atomically replaces it.</summary>
    public void SaveToFile(SystemicGame game, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var json = Save(game);
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        var temporaryCreated = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                temporaryCreated = true;
                stream.Write(Encoding.UTF8.GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            temporaryCreated = false;
        }
        finally
        {
            if (temporaryCreated)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Loads a size-limited UTF-8 save file using the same validation as <see cref="Load"/>.</summary>
    public MaterializedContentWorld LoadFromFile(ContentPack content, string path)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            var bytes = ContentFileReader.ReadBytes(path, path);
            var json = new UTF8Encoding(false, true).GetString(bytes);
            return Load(content, json);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ContentPackException or DecoderFallbackException)
        {
            throw new SystemicSaveException($"Could not read save file: {exception.Message}", exception);
        }
    }

    public MaterializedContentWorld Load(ContentPack content, string json)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > ContentFileReader.MaximumFileBytes)
            {
                throw new SystemicSaveException("Save document exceeds the 64 MiB limit.");
            }

            var migratedJson = Migrate(json);
            var migratedBytes = Encoding.UTF8.GetBytes(migratedJson);
            var diagnostics = ContentSchemaValidator.Validate(SaveSchemaId, s_saveSchema, migratedBytes, "save.json");
            if (!diagnostics.IsEmpty)
            {
                var formatted = string.Join(Environment.NewLine, diagnostics.Select(diagnostic =>
                    $"{diagnostic.File}:{diagnostic.Path} [{diagnostic.Code}] {diagnostic.Message}"));
                throw new SystemicSaveException($"Current save document does not match its schema:{Environment.NewLine}{formatted}");
            }

            var document = JsonSerializer.Deserialize(migratedJson, SystemicSaveJsonContext.Default.SaveDocument)
                ?? throw new SystemicSaveException("Save document is empty.");
            ValidateSemantics(document);

            var contentFingerprint = RequireContentFingerprint(content);
            if (!string.Equals(document.ContentFingerprint, contentFingerprint, StringComparison.Ordinal))
            {
                throw new SystemicSaveException("Save content fingerprint does not match the loaded content pack.");
            }

            var clockState = document.ExtensionStates![ClockStatefulExtension.ExtensionId]
                .Deserialize(SystemicSaveJsonContext.Default.FixedStepClockState);
            var clock = new FixedStepClock(content.Manifest.TickRateHz, content.Manifest.MaxCatchUpSteps);
            clock.RestoreState(clockState);

            var worldState = document.World;
            var world = new World(seed: worldState.Seed, valueScale: worldState.ValueScale,
                spatialCellSize: worldState.SpatialCellSize);
            world.Draws.RestoreState(worldState.Draws);
            world.LogicalTick = worldState.LogicalTick;
            // WHY: a tick-zero load does not enter the in-progress phase through the setter, and
            // RemainingFromExpiry is paired with the in-progress formula below, so set it explicitly
            // before any absolute expiry is converted back to a remaining duration.
            world.RestoreModifierTickPhase(inProgress: true);
            foreach (var savedEntity in worldState.Entities.OrderBy(entity => entity.Id))
            {
                var codec = _codecs[savedEntity.Codec];
                var entity = codec.Decode(savedEntity.Payload)
                    ?? throw new SystemicSaveException($"Entity codec '{codec.Id}' returned null for entity {savedEntity.Id}.");
                entity.RestoreBaseState(savedEntity.Tags, savedEntity.Capabilities,
                    savedEntity.Links.OrderBy(pair => pair.Key, StringComparer.Ordinal), savedEntity.ToRemove);
                world.AddRestored(entity, savedEntity.Id);
            }

            foreach (var savedEntity in worldState.Entities)
            {
                if (savedEntity.SpatialBounds is { } bounds)
                {
                    world.SetSpatialBounds(world.ById(savedEntity.Id)!, bounds);
                }
            }

            foreach (var modifier in worldState.Modifiers)
            {
                var remainingTicks = RemainingFromExpiry(modifier.ExpiryTick, world.LogicalTick);
                if (modifier.SourceProperty is { } sourceProperty)
                {
                    world.SetModifierFromProperty(modifier.Source, world.ById(modifier.EntityId)!, modifier.Property,
                        modifier.Operation, modifier.Value, sourceProperty, remainingTicks,
                        modifier.Priority, modifier.Stage);
                }
                else
                {
                    world.SetModifier(modifier.Source, world.ById(modifier.EntityId)!, modifier.Property,
                        modifier.Operation, modifier.Value, remainingTicks, modifier.Priority, modifier.Stage);
                }
            }

            foreach (var grant in worldState.Grants)
            {
                world.Grant(grant.Source, world.ById(grant.EntityId)!, grant.Kind, grant.Name,
                    RemainingFromExpiry(grant.ExpiryTick, world.LogicalTick));
            }

            // Absolute expiries are restored above through the in-progress formula, so the phase must
            // only end here: a completed save is between ticks, and effects created after the load
            // start at the next tick.
            world.RestoreModifierTickPhase(inProgress: false);

            foreach (var (role, entityId) in worldState.BoundRoles.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                world.BindRole(role, world.ById(entityId)!);
            }

            foreach (var entityId in worldState.RequiredEntityIds)
            {
                world.MarkRequired(world.ById(entityId)!);
            }

            world.RestoreEntityIdSequence(worldState.NextEntityId);
            var entityIds = document.EntityIds.ToImmutableDictionary(StringComparer.Ordinal);
            var extensionStates = (document.ExtensionStates
                ?? throw new SystemicSaveException("Save document requires extensionStates."))
                .ToImmutableDictionary(
                pair => pair.Key, pair => CloneJson(pair.Value), StringComparer.Ordinal);
            return new MaterializedContentWorld(world, entityIds, extensionStates);
        }
        catch (SystemicSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            throw new SystemicSaveException($"Could not load save: {exception.Message}", exception);
        }
    }

    private SavedEntity CaptureEntity(World world, Entity entity)
    {
        var codec = FindCodec(entity);
        var links = entity.Links.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var bounds = world.TryGetSpatialBounds(entity.Id, out var spatialBounds) ? spatialBounds : (Bounds2?)null;
        return new SavedEntity(entity.Id, codec.Id, CloneJson(codec.Encode(entity)),
            [.. entity.Tags.Order(StringComparer.Ordinal)],
            [.. entity.Capabilities.Order(StringComparer.Ordinal)],
            links, entity.ToRemove, bounds);
    }

    private IEntityStateCodec FindCodec(Entity entity)
    {
        IEntityStateCodec? match = null;
        foreach (var codec in _codecs.Values)
        {
            if (!codec.CanEncode(entity))
            {
                continue;
            }

            if (match is not null)
            {
                throw new SystemicSaveException($"More than one entity save codec accepts {entity.GetType().Name}.");
            }

            match = codec;
        }

        return match ?? throw new SystemicSaveException($"No entity save codec accepts {entity.GetType().Name} (ID {entity.Id}).");
    }

    private string Migrate(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("schemaVersion"u8, out var versionNode)
            || versionNode.ValueKind != JsonValueKind.Number
            || !versionNode.TryGetInt32(out var version))
        {
            throw new SystemicSaveException("Save document requires an integer schemaVersion.");
        }

        // Version 5 renamed the modifier duration field to an absolute expiry tick, so a version 4
        // document is rejected rather than silently reinterpreted. Version 6 retired the typed event
        // engine and upgrades from version 5 by dropping its counters.
        if (version < 5)
        {
            throw new SystemicSaveException($"Save schema version {version} is no longer supported. Create a new version {CurrentSchemaVersion} save using the current content.");
        }

        if (version > CurrentSchemaVersion)
        {
            throw new SystemicSaveException($"Save schema version {version} is newer than supported version {CurrentSchemaVersion}.");
        }
        SaveIntegrity.Validate(document.RootElement);
        return version < CurrentSchemaVersion
            ? SystemicSaveMigration.Upgrade(json, CurrentSchemaVersion, _migrations.Values)
            : json;
    }

    // World stores absolute expiry ticks; saves carry the same absolute tick, and the setter that
    // accepts a duration turns it back into the equivalent duration at the restored tick.
    private static int RemainingFromExpiry(int expiryTick, int logicalTick) =>
        expiryTick < 0 ? -1 : expiryTick - logicalTick + 1;

    private static string RequireContentFingerprint(ContentPack content)
    {
        if (!SaveIntegrity.IsDigest(content.ContentFingerprint))
        {
            throw new SystemicSaveException("Content pack has no valid SHA-256 fingerprint. Supply ContentFingerprint for synthetic or modified content.");
        }
        return content.ContentFingerprint!;
    }

    private static string ReadSchema()
    {
        using var stream = typeof(SystemicSaveCodec).Assembly.GetManifestResourceStream(
            "Systemic.Core.Schemas.save.schema.json")
            ?? throw new InvalidOperationException("Embedded save schema was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // JSON Schema checks each value's shape and range before deserialization. This pass is
    // reserved for invariants that depend on meaning or relationships between saved values.
    private static void ValidateSemantics(SaveDocument document)
    {
        var world = document.World;
        if (!double.IsFinite(world.ValueScale) || !float.IsFinite(world.SpatialCellSize))
        {
            throw new SystemicSaveException("Save world state contains a non-finite value scale or spatial cell size.");
        }

        if ((world.Draws.Increment & 1UL) == 0)
        {
            throw new SystemicSaveException("Save contains an invalid random stream state.");
        }

        var entityIds = new HashSet<int>();
        var maximumId = 0;
        foreach (var entity in world.Entities)
        {
            if (!entityIds.Add(entity.Id)
                || string.IsNullOrWhiteSpace(entity.Codec)
                || entity.Payload.ValueKind == JsonValueKind.Undefined
                || entity.Tags.Any(string.IsNullOrWhiteSpace)
                || entity.Capabilities.Any(string.IsNullOrWhiteSpace)
                || entity.Links.Keys.Any(string.IsNullOrWhiteSpace))
            {
                throw new SystemicSaveException("Save contains an invalid or duplicate entity record.");
            }

            maximumId = Math.Max(maximumId, entity.Id);
            if (entity.SpatialBounds is { IsValid: false })
            {
                throw new SystemicSaveException($"Entity {entity.Id} has invalid spatial bounds.");
            }
        }

        if (world.NextEntityId <= maximumId)
        {
            throw new SystemicSaveException("Next entity ID must be greater than every saved entity ID.");
        }

        foreach (var entity in world.Entities)
        {
            if (entity.Links.Any(link => !entityIds.Contains(link.Value)))
            {
                throw new SystemicSaveException($"Entity {entity.Id} links to an entity that is not in the save.");
            }
        }

        foreach (var id in world.RequiredEntityIds)
        {
            RequireSavedEntity(entityIds, id, "required entity");
        }

        foreach (var role in world.BoundRoles)
        {
            Require(!string.IsNullOrWhiteSpace(role.Key), "Save contains an empty role name.");
            RequireSavedEntity(entityIds, role.Value, $"role '{role.Key}'");
        }

        foreach (var modifier in world.Modifiers)
        {
            RequireSavedEntity(entityIds, modifier.EntityId, "modifier");
            Require(!string.IsNullOrWhiteSpace(modifier.Source) && !string.IsNullOrWhiteSpace(modifier.Property)
                && double.IsFinite(modifier.Value)
                && (modifier.SourceProperty is null || !string.IsNullOrWhiteSpace(modifier.SourceProperty)),
                "Save contains an invalid property modifier.");
        }

        foreach (var grant in world.Grants)
        {
            RequireSavedEntity(entityIds, grant.EntityId, "grant");
            Require(!string.IsNullOrWhiteSpace(grant.Source) && !string.IsNullOrWhiteSpace(grant.Name),
                "Save contains an invalid property grant.");
        }

        foreach (var (contentId, entityId) in document.EntityIds)
        {
            Require(IsStableId(contentId), $"Save contains invalid content entity ID '{contentId}'.");
            RequireSavedEntity(entityIds, entityId, $"content entity '{contentId}'");
        }

        foreach (var extensionId in document.ExtensionStates!.Keys)
        {
            Require(IsStableId(extensionId), $"Save contains invalid extension ID '{extensionId}'.");
        }
    }

    private static void RequireSavedEntity(HashSet<int> entityIds, int id, string context)
    {
        if (!entityIds.Contains(id))
        {
            throw new SystemicSaveException($"Save {context} references missing entity {id}.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new SystemicSaveException(message);
        }
    }

    private static bool IsStableId(string value)
    {
        if (value.Length == 0 || value[0] is < 'a' or > 'z')
        {
            return false;
        }

        var previousSeparator = false;
        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                previousSeparator = false;
                continue;
            }

            if (character is '.' or '-')
            {
                if (previousSeparator || index == value.Length - 1)
                {
                    return false;
                }

                previousSeparator = true;
                continue;
            }

            return false;
        }

        return true;
    }

    private static JsonElement CloneJson(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Undefined)
        {
            throw new SystemicSaveException("Save state codecs must return a defined JSON value.");
        }

        return element.Clone();
    }

    private sealed class DataEntityStateCodec : IEntityStateCodec
    {
        public const string CodecId = "kernel.data";
        public string Id => CodecId;
        public bool CanEncode(Entity entity) => entity is DataEntity;

        public JsonElement Encode(Entity entity)
        {
            var dataEntity = (DataEntity)entity;
            var properties = dataEntity.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            return JsonSerializer.SerializeToElement(properties, SystemicSaveJsonContext.Default.DictionaryStringDouble);
        }

        public Entity Decode(JsonElement payload)
        {
            var properties = payload.Deserialize(SystemicSaveJsonContext.Default.DictionaryStringDouble)
                ?? throw new SystemicSaveException("Data entity save payload is empty.");
            if (properties.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || !double.IsFinite(pair.Value)))
            {
                throw new SystemicSaveException("Data entity save payload has invalid properties.");
            }

            return new DataEntity([], [], [.. properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value))]);
        }
    }

}

internal sealed record SaveDocument(
    int SchemaVersion,
    string ContentFingerprint,
    string Checksum,
    SavedWorld World,
    Dictionary<string, int> EntityIds,
    Dictionary<string, JsonElement>? ExtensionStates = null);

internal sealed record SavedWorld(
    int Seed,
    double ValueScale,
    float SpatialCellSize,
    int LogicalTick,
    int NextEntityId,
    DrawStreamSnapshot Draws,
    SavedEntity[] Entities,
    PropertyModifier[] Modifiers,
    PropertyGrant[] Grants,
    Dictionary<string, int> BoundRoles,
    int[] RequiredEntityIds);

internal sealed record SavedEntity(
    int Id,
    string Codec,
    JsonElement Payload,
    string[] Tags,
    string[] Capabilities,
    Dictionary<string, int> Links,
    bool ToRemove,
    Bounds2? SpatialBounds);

internal sealed record RuleHistoryState(
    RuleEventHistorySample[] Samples,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RuleEngine.RuleScheduledFactState[]? Scheduled = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    RuleEngine.RuleCooldownState[]? Cooldowns = null);
