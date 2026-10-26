using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    public const int CurrentSchemaVersion = 3;
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

        _migrations = new Dictionary<int, ISaveMigration>
        {
            [1] = new AddPresentationMetadataMigration(),
            [2] = new MovePresentationStateToExtensionsMigration()
        };
        foreach (var migration in migrations ?? [])
        {
            ArgumentNullException.ThrowIfNull(migration);
            if (migration.FromVersion < 1 || migration.FromVersion >= CurrentSchemaVersion
                || !_migrations.TryAdd(migration.FromVersion, migration))
            {
                throw new ArgumentException($"Save migration from version {migration.FromVersion} is invalid or duplicated.", nameof(migrations));
            }
        }
    }

    public string Save(SystemicGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var world = game.World;
        var entities = world.Entities.OrderBy(entity => entity.Id)
            .Select(entity => CaptureEntity(world, entity))
            .ToArray();
        var document = new SaveDocument(
            CurrentSchemaVersion,
            new SavedWorld(
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
            game.EntityIds.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            game.StatefulExtensions.ToDictionary(extension => extension.Id,
                extension => CloneJson(extension.CaptureState()), StringComparer.Ordinal));

        return JsonSerializer.Serialize(document, SystemicSaveJsonContext.Default.SaveDocument);
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

            var worldState = document.World;
            var world = new World(seed: 0, valueScale: worldState.ValueScale,
                spatialCellSize: worldState.SpatialCellSize);
            world.Draws.RestoreState(worldState.Draws);
            world.LogicalTick = worldState.LogicalTick;
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
                world.SetModifier(modifier.Source, world.ById(modifier.EntityId)!, modifier.Property,
                    modifier.Operation, modifier.Value, modifier.RemainingTicks, modifier.Priority, modifier.Stage);
            }

            foreach (var grant in worldState.Grants)
            {
                world.Grant(grant.Source, world.ById(grant.EntityId)!, grant.Kind, grant.Name,
                    grant.RemainingTicks);
            }

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
        var document = JsonNode.Parse(json)?.AsObject()
            ?? throw new SystemicSaveException("Save document must be a JSON object.");
        if (!int.TryParse(document["schemaVersion"]?.ToString(), out var version))
        {
            throw new SystemicSaveException("Save document requires an integer schemaVersion.");
        }

        if (version < 1 || version > CurrentSchemaVersion)
        {
            throw new SystemicSaveException($"Save schema version {version} is not supported.");
        }

        while (version < CurrentSchemaVersion)
        {
            if (!_migrations.TryGetValue(version, out var migration))
            {
                throw new SystemicSaveException($"No save migration is registered from version {version} to {version + 1}.");
            }

            migration.Apply(document);
            version++;
            document["schemaVersion"] = version;
        }

        return document.ToJsonString();
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
                && double.IsFinite(modifier.Value),
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

    private sealed class AddPresentationMetadataMigration : ISaveMigration
    {
        public int FromVersion => 1;

        public void Apply(JsonObject document)
        {
            document["presentation"] ??= new JsonObject
            {
                ["visualRootSeed"] = 0,
                ["nextActorVisualId"] = 0
            };
            document["extensionStates"] ??= new JsonObject();
        }
    }

    private sealed class MovePresentationStateToExtensionsMigration : ISaveMigration
    {
        public int FromVersion => 2;

        public void Apply(JsonObject document)
        {
            JsonObject extensionStates;
            if (document.TryGetPropertyValue("extensionStates", out var extensionStatesNode))
            {
                extensionStates = extensionStatesNode as JsonObject
                    ?? throw new SystemicSaveException("Version 2 save extensionStates must be a JSON object.");
            }
            else
            {
                extensionStates = [];
            }

            if (document["presentation"] is { } presentation
                && !extensionStates.ContainsKey("systemic.presentation.identity"))
            {
                extensionStates["systemic.presentation.identity"] = presentation.DeepClone();
            }

            document.Remove("presentation");
            document["extensionStates"] = extensionStates;
        }
    }
}

internal sealed record SaveDocument(
    int SchemaVersion,
    SavedWorld World,
    Dictionary<string, int> EntityIds,
    Dictionary<string, JsonElement>? ExtensionStates = null);

internal sealed record SavedWorld(
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

internal sealed record RuleHistoryState(RuleEventHistorySample[] Samples);
