using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Systemic.Kernel;

namespace Systemic.Gameplay2D;

public sealed class Gameplay2DContent(
    ImmutableDictionary<string, ActorDefinition> actors,
    ImmutableDictionary<string, StatusEffectDefinition> statuses) : Systemic.Content.ISystemicModuleContent
{
    public ImmutableDictionary<string, ActorDefinition> Actors { get; } = actors;

    public ImmutableDictionary<string, StatusEffectDefinition> Statuses { get; } = statuses;
}

public sealed class Gameplay2DContentException(
    string sourceFile,
    string contentPath,
    string message,
    Exception? innerException = null)
    : Exception($"{sourceFile}: {message} at {contentPath}", innerException)
{
    public string SourceFile { get; } = sourceFile;

    public string ContentPath { get; } = contentPath;
}

/// <summary>Loads bounded, engine-neutral actor and status definitions for the optional 2D toolkit.</summary>
public static class Gameplay2DContentLoader
{
    private const int MaximumJsonCharacters = 1_000_000;
    private const int MaximumDefinitions = 1_024;
    private const int MaximumTraitsPerActor = 256;
    private const int MaximumPropertiesPerActor = 256;
    private const int MaximumStringLength = 128;

    public static Gameplay2DContent Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumJsonCharacters)
            {
                throw Error(path, "$", $"Gameplay2D content exceeds the {MaximumJsonCharacters:N0}-character limit.");
            }

            var bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw Error(path, "$", $"Gameplay2D content exceeds the {MaximumJsonCharacters:N0}-character limit.");
            }

            return Parse(Encoding.UTF8.GetString(bytes), path);
        }
        catch (Gameplay2DContentException)
        {
            throw;
        }
        catch (IOException exception)
        {
            throw Error(path, "$", "Could not read Gameplay2D content.", exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw Error(path, "$", "Could not read Gameplay2D content.", exception);
        }
    }

    public static Gameplay2DContent Parse(string json, string sourceFile = "gameplay2d.json")
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFile);
        if (json.Length > MaximumJsonCharacters)
        {
            throw Error(sourceFile, "$", $"Gameplay2D content exceeds the {MaximumJsonCharacters:N0}-character limit.");
        }

        Gameplay2DFile file;
        try
        {
            file = JsonSerializer.Deserialize(json, Gameplay2DContentJsonContext.Default.Gameplay2DFile)
                ?? throw Error(sourceFile, "$", "Gameplay2D content must be a JSON object.");
        }
        catch (JsonException exception)
        {
            throw Error(sourceFile, exception.Path ?? "$", "Gameplay2D content is not valid JSON for schema version 2.", exception);
        }

        if (file.SchemaVersion != 2)
        {
            throw Error(sourceFile, "$.schemaVersion", "Gameplay2D content requires schemaVersion 2.");
        }

        var actors = file.Actors ?? throw Error(sourceFile, "$.actors", "Actor definitions are required.");
        var statuses = file.Statuses ?? throw Error(sourceFile, "$.statuses", "Status definitions are required.");
        RequireLimit(actors.Length, MaximumDefinitions, sourceFile, "$.actors", "actor definitions");
        RequireLimit(statuses.Length, MaximumDefinitions, sourceFile, "$.statuses", "status definitions");

        var actorDefinitions = ImmutableDictionary.CreateBuilder<string, ActorDefinition>(StringComparer.Ordinal);
        for (var index = 0; index < actors.Length; index++)
        {
            var path = $"$.actors[{index}]";
            var actor = actors[index] ?? throw Error(sourceFile, path, "Actor definition cannot be null.");
            var id = RequireId(actor.Id, sourceFile, $"{path}.id");
            var tags = RequireTraits(actor.Tags, sourceFile, $"{path}.tags");
            var capabilities = RequireTraits(actor.Capabilities, sourceFile, $"{path}.capabilities");
            var properties = actor.Properties
                ?? throw Error(sourceFile, $"{path}.properties", "Actor properties are required.");
            RequireLimit(properties.Count, MaximumPropertiesPerActor, sourceFile,
                $"{path}.properties", "actor properties");
            var halfWidth = actor.HalfWidth
                ?? throw Error(sourceFile, $"{path}.halfWidth", "Actor halfWidth is required.");
            var halfHeight = actor.HalfHeight
                ?? throw Error(sourceFile, $"{path}.halfHeight", "Actor halfHeight is required.");

            if (!float.IsFinite(halfWidth) || halfWidth is < 0 or > 4096)
            {
                throw Error(sourceFile, $"{path}.halfWidth", "Actor halfWidth must be between 0 and 4096.");
            }

            if (!float.IsFinite(halfHeight) || halfHeight is < 0 or > 4096)
            {
                throw Error(sourceFile, $"{path}.halfHeight", "Actor halfHeight must be between 0 and 4096.");
            }

            var immutableProperties = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
            foreach (var (name, value) in properties)
            {
                var propertyPath = $"{path}.properties.{name}";
                RequireName(name, sourceFile, propertyPath);
                if (IsReservedProperty(name))
                {
                    throw Error(sourceFile, propertyPath, "Actor property is reserved for 2D spatial state.");
                }

                if (!double.IsFinite(value))
                {
                    throw Error(sourceFile, propertyPath, "Actor property value must be finite.");
                }

                immutableProperties.Add(name, value);
            }

            var definition = new ActorDefinition(id, tags, capabilities, immutableProperties.ToImmutable(),
                halfWidth, halfHeight);
            if (!actorDefinitions.TryAdd(id, definition))
            {
                throw Error(sourceFile, $"{path}.id", $"Duplicate actor ID '{id}'.");
            }
        }

        var statusDefinitions = ImmutableDictionary.CreateBuilder<string, StatusEffectDefinition>(StringComparer.Ordinal);
        for (var index = 0; index < statuses.Length; index++)
        {
            var path = $"$.statuses[{index}]";
            var status = statuses[index] ?? throw Error(sourceFile, path, "Status definition cannot be null.");
            var id = RequireId(status.Id, sourceFile, $"{path}.id");
            var tag = RequireName(status.Tag, sourceFile, $"{path}.tag");
            var property = RequireName(status.Property, sourceFile, $"{path}.property");
            if (IsReservedProperty(property))
            {
                throw Error(sourceFile, $"{path}.property", "Status property cannot modify reserved 2D spatial state.");
            }

            var value = status.Value
                ?? throw Error(sourceFile, $"{path}.value", "Status value is required.");
            if (!double.IsFinite(value))
            {
                throw Error(sourceFile, $"{path}.value", "Status value must be finite.");
            }

            if (status.DurationTicks is < 1 or > 1_000_000)
            {
                throw Error(sourceFile, $"{path}.durationTicks", "Status durationTicks must be between 1 and 1,000,000.");
            }

            if (status.MaximumStacks is < 1 or > 64)
            {
                throw Error(sourceFile, $"{path}.maximumStacks", "Status maximumStacks must be between 1 and 64.");
            }

            var operation = ParseOperation(status.Operation, sourceFile, $"{path}.operation");
            var stage = ParseStage(status.Stage, sourceFile, $"{path}.stage");
            var stacking = ParseStacking(status.Stacking, sourceFile, $"{path}.stacking");
            var definition = new StatusEffectDefinition(id, tag, property, operation, value,
                status.DurationTicks, stage, stacking, status.MaximumStacks);
            if (!statusDefinitions.TryAdd(id, definition))
            {
                throw Error(sourceFile, $"{path}.id", $"Duplicate status ID '{id}'.");
            }
        }

        return new Gameplay2DContent(actorDefinitions.ToImmutable(), statusDefinitions.ToImmutable());
    }

    private static ImmutableArray<string> RequireTraits(string[]? values, string sourceFile, string path)
    {
        var traits = values ?? throw Error(sourceFile, path, "Trait list is required.");
        RequireLimit(traits.Length, MaximumTraitsPerActor, sourceFile, path, "actor traits");
        var unique = ImmutableArray.CreateBuilder<string>(traits.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < traits.Length; index++)
        {
            var trait = RequireName(traits[index], sourceFile, $"{path}[{index}]");
            if (!seen.Add(trait))
            {
                throw Error(sourceFile, $"{path}[{index}]", $"Duplicate trait '{trait}'.");
            }

            unique.Add(trait);
        }

        return unique.MoveToImmutable();
    }

    private static string RequireId(string? value, string sourceFile, string path)
    {
        var id = RequireName(value, sourceFile, path);
        if (!IsValidId(id))
        {
            throw Error(sourceFile, path, $"ID '{id}' must start with a lowercase letter and use lowercase letters, digits, dots, or hyphens.");
        }

        return id;
    }

    private static string RequireName(string? value, string sourceFile, string path)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumStringLength || value != value.Trim())
        {
            throw Error(sourceFile, path, $"Value must be a trimmed non-empty string of at most {MaximumStringLength} characters.");
        }

        return value;
    }

    private static bool IsValidId(string value)
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

            if (character is not ('.' or '-') || previousSeparator || index == value.Length - 1)
            {
                return false;
            }

            previousSeparator = true;
        }

        return true;
    }

    private static ModifierOperation ParseOperation(string? value, string sourceFile, string path) => value switch
    {
        "add" => ModifierOperation.Add,
        "multiply" => ModifierOperation.Multiply,
        "set" => ModifierOperation.Set,
        _ => throw Error(sourceFile, path, $"Unknown status operation '{value}'."),
    };

    private static ModifierStage ParseStage(string? value, string sourceFile, string path) => value switch
    {
        null or "standard" => ModifierStage.Standard,
        "foundation" => ModifierStage.Foundation,
        "additive" => ModifierStage.Additive,
        "multiplicative" => ModifierStage.Multiplicative,
        "override" => ModifierStage.Override,
        "final" => ModifierStage.Final,
        _ => throw Error(sourceFile, path, $"Unknown modifier stage '{value}'."),
    };

    private static StatusStackingMode ParseStacking(string? value, string sourceFile, string path) => value switch
    {
        null or "refresh" => StatusStackingMode.Refresh,
        "stack" => StatusStackingMode.Stack,
        _ => throw Error(sourceFile, path, $"Unknown status stacking mode '{value}'."),
    };

    private static void RequireLimit(int count, int maximum, string sourceFile, string path, string label)
    {
        if (count > maximum)
        {
            throw Error(sourceFile, path, $"The limit is {maximum:N0} {label}.");
        }
    }

    private static bool IsReservedProperty(string name) => name is Gameplay2D.PositionX or Gameplay2D.PositionY
        or Gameplay2D.HalfWidth or Gameplay2D.HalfHeight or Gameplay2D.VelocityX or Gameplay2D.VelocityY
        or Gameplay2D.RemainingTicks;

    private static Gameplay2DContentException Error(
        string sourceFile,
        string path,
        string message,
        Exception? innerException = null) => new(sourceFile, path, message, innerException);
}

internal sealed record Gameplay2DFile(
    int SchemaVersion,
    Gameplay2DActorFile[]? Actors,
    Gameplay2DStatusFile[]? Statuses);

internal sealed record Gameplay2DActorFile(
    string? Id,
    string[]? Tags,
    string[]? Capabilities,
    Dictionary<string, double>? Properties,
    float? HalfWidth,
    float? HalfHeight);

internal sealed record Gameplay2DStatusFile(
    string? Id,
    string? Tag,
    string? Property,
    string? Operation,
    double? Value,
    int DurationTicks,
    string? Stage = null,
    string? Stacking = null,
    int MaximumStacks = 1);

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    MaxDepth = 32,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(Gameplay2DFile))]
internal partial class Gameplay2DContentJsonContext : JsonSerializerContext;
