using System.Collections.Immutable;
using System.Text.Json;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Content;

public sealed record GameManifest(string Id, string Name, int TickRateHz, int MaxCatchUpSteps);

public sealed record EntityDefinition(
    string Id,
    string Kind,
    ImmutableArray<string> Tags,
    ImmutableArray<string> Capabilities,
    ImmutableDictionary<string, double> Properties,
    ImmutableDictionary<string, string> Links,
    string? VisualRecipeId,
    Bounds2? SpatialBounds)
{
    public ImmutableDictionary<string, ContentFieldSource> FieldSources { get; init; } =
        [];
}

public sealed record ContentFieldSource(string File, string Path);

public sealed record CompiledEntities(ImmutableArray<EntityDefinition> Definitions);

public sealed record MaterializedContentWorld(
    World World,
    ImmutableDictionary<string, int> EntityIds,
    ImmutableDictionary<string, JsonElement>? ExtensionStates = null);

/// <summary>Compiled core content plus opaque content produced by registered optional modules.</summary>
public sealed record ContentPack(
    string RootDirectory,
    GameManifest Manifest,
    CompiledRuleSet Rules,
    CompiledEntities Entities,
    ImmutableArray<CompiledSystemicModule> Modules = default)
{
    /// <summary>Creates the file-authored initial population and resolves stable content links to runtime IDs.</summary>
    public MaterializedContentWorld CreateWorld(
        int seed,
        double valueScale,
        float spatialCellSize = World.DefaultSpatialCellSize,
        ImmutableDictionary<string, JsonElement>? extensionStates = null)
    {
        var world = new World(seed, valueScale, spatialCellSize);
        var ids = ImmutableDictionary.CreateBuilder<string, int>(StringComparer.Ordinal);
        foreach (var definition in Entities.Definitions)
        {
            var properties = definition.Properties
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (pair.Key, pair.Value))
                .ToArray();
            var entity = world.Add(new DataEntity(definition.Tags, definition.Capabilities, properties));
            ids.Add(definition.Id, entity.Id);
        }

        foreach (var definition in Entities.Definitions)
        {
            var entity = world.ById(ids[definition.Id])!;
            foreach (var (link, target) in definition.Links.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                entity.SetLink(link, ids[target]);
            }

            if (definition.SpatialBounds is { } bounds)
            {
                world.SetSpatialBounds(entity, bounds);
            }
        }

        return new MaterializedContentWorld(world, ids.ToImmutable(), extensionStates);
    }
}

public sealed record ContentDiagnostic(string Code, string File, string Path, string Message);

public sealed class ContentPackException : Exception
{
    public ContentPackException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Diagnostics = [];
    }

    public ContentPackException(IEnumerable<ContentDiagnostic> diagnostics)
        : this([.. diagnostics])
    {
    }

    private ContentPackException(ImmutableArray<ContentDiagnostic> diagnostics)
        : base(FormatDiagnostics(diagnostics))
    {
        Diagnostics = diagnostics;
    }

    public ImmutableArray<ContentDiagnostic> Diagnostics { get; }
    public string? FieldPath { get; }

    public ContentPackException(string message, string fieldPath, Exception? innerException = null)
        : this(message, innerException)
    {
        FieldPath = fieldPath;
    }

    private static string FormatDiagnostics(ImmutableArray<ContentDiagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(diagnostic =>
            $"{diagnostic.File}:{diagnostic.Path} [{diagnostic.Code}] {diagnostic.Message}"));
}
