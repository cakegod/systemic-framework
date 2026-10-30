using System.Collections.Immutable;

namespace Systemic.Content;

/// <summary>
/// A compiled, schema-validated reload candidate. Creating one does not replace a
/// running game or migrate its world, systems, services, or extension state.
/// </summary>
public sealed class ContentReloadCandidate
{
    private readonly ContentPack _source;

    internal ContentReloadCandidate(ContentPack source, ContentPack content)
    {
        _source = source;
        SourceFingerprint = source.ContentFingerprint!;
        Content = content;
    }

    public string SourceFingerprint { get; }
    public ContentPack Content { get; }

    /// <summary>
    /// Checks the original pack instance and its captured identity. A future host
    /// must perform this check and any swap atomically under its own lifecycle lock.
    /// </summary>
    public bool IsBasedOn(ContentPack active) => ReferenceEquals(_source, active)
        && string.Equals(SourceFingerprint, active.ContentFingerprint, StringComparison.Ordinal);
}

/// <summary>The candidate, or diagnostics explaining why it could not be prepared.</summary>
public sealed class ContentReloadResult
{
    internal ContentReloadResult(ContentReloadCandidate? candidate, ImmutableArray<ContentDiagnostic> diagnostics)
    {
        Candidate = candidate;
        Diagnostics = diagnostics;
    }

    public bool Succeeded => Candidate is not null;
    public ContentReloadCandidate? Candidate { get; }
    public ImmutableArray<ContentDiagnostic> Diagnostics { get; }
}

/// <summary>Compiles and checks candidate content without mutating the active game.</summary>
public static class ContentReload
{
    /// <summary>
    /// Prepares a candidate using fresh module instances. Module compilation must be
    /// isolated from live state and free of external side effects: Core cannot roll
    /// back effects performed by arbitrary module code. A successful candidate still
    /// requires domain-specific state migration and a host-managed atomic transition.
    /// </summary>
    /// <param name="active">The active content pack whose identity is captured by the candidate.</param>
    /// <param name="rootDirectory">Directory containing candidate content files.</param>
    /// <param name="modules">Fresh, isolated module instances used to compile candidate content.</param>
    /// <param name="tuningSchemas">Current typed-tuning schemas required by the active pack, keyed by entity ID.</param>
    /// <returns>The compiled candidate, or diagnostics explaining why preparation failed.</returns>
    public static ContentReloadResult Prepare(ContentPack active, string rootDirectory,
        IEnumerable<ISystemicContentModule>? modules = null,
        IReadOnlyDictionary<string, ContentTuningSchema>? tuningSchemas = null)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!IsFingerprint(active.ContentFingerprint))
        {
            return Failure("reload.source_fingerprint_missing", "game.json", "$",
                "The active content needs a valid SHA-256 ContentFingerprint before preparing a reload.");
        }

        if (!active.TuningDocuments.IsEmpty && tuningSchemas is null)
        {
            return Failure("reload.tuning_schemas_missing", "entities.json", "$.entities",
                "Reloading content with typed tuning requires the current tuning schemas.");
        }

        var activeTuningIds = active.TuningDocuments.Keys.ToHashSet(StringComparer.Ordinal);
        var candidateTuningIds = tuningSchemas?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
        if (!activeTuningIds.SetEquals(candidateTuningIds))
        {
            return Failure("reload.tuning_schema_mismatch", "entities.json", "$.entities",
                "The candidate must use the same set of tuning schemas as the active content.");
        }

        var registry = new ContentModuleRegistry();
        try
        {
            if (tuningSchemas is not null)
            {
                foreach (var (entityId, schema) in tuningSchemas.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    registry.AddTuningSchema(entityId, schema);
                }
            }

            foreach (var module in modules ?? [])
            {
                if (module is null)
                {
                    return Failure("reload.module_invalid", "game.json", "$.modules", "A reload module instance is null.");
                }

                if (!active.Modules.IsDefaultOrEmpty && active.Modules.Any(compiled => ReferenceEquals(compiled.Module, module)))
                {
                    return Failure("reload.module_instance_reused", "game.json", "$.modules",
                        $"Module '{module.Id}' is used by the active pack; provide a fresh isolated module instance.");
                }

                registry.Add(module);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure("reload.module_invalid", "game.json", "$.modules", exception.Message);
        }

        ContentPack candidate;
        try
        {
            candidate = ContentPackLoader.Load(rootDirectory, registry);
        }
        catch (ContentPackException exception)
        {
            return exception.Diagnostics.IsEmpty
                ? Failure("reload.compile_failed", "game.json", exception.FieldPath ?? "$", exception.Message)
                : new ContentReloadResult(null, exception.Diagnostics);
        }
        catch (ContentTuningException exception)
        {
            return new ContentReloadResult(null, [.. exception.Diagnostics.Select(diagnostic =>
                new ContentDiagnostic(diagnostic.Code, diagnostic.File, diagnostic.Path, diagnostic.Message,
                    diagnostic.Line, diagnostic.Column))]);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or OperationCanceledException))
        {
            return Failure("reload.compile_failed", "game.json", "$",
                $"Candidate compilation failed ({exception.GetType().Name}): {exception.Message}");
        }

        var diagnostics = ImmutableArray.CreateBuilder<ContentDiagnostic>();
        if (!string.Equals(active.Manifest.Id, candidate.Manifest.Id, StringComparison.Ordinal))
        {
            diagnostics.Add(new("reload.identity_mismatch", "game.json", "$.id",
                $"Active game ID '{active.Manifest.Id}' differs from candidate '{candidate.Manifest.Id}'."));
        }

        if (active.Manifest.TickRateHz != candidate.Manifest.TickRateHz
            || active.Manifest.MaxCatchUpSteps != candidate.Manifest.MaxCatchUpSteps)
        {
            diagnostics.Add(new("reload.clock_mismatch", "game.json", "$",
                "The candidate changes tickRateHz or maxCatchUpSteps; a clock migration is required."));
        }

        if (!IsFingerprint(candidate.ContentFingerprint))
        {
            diagnostics.Add(new("reload.candidate_fingerprint_missing", "game.json", "$",
                "The compiled candidate has no valid SHA-256 fingerprint."));
        }

        CheckEntities(active, candidate, diagnostics);
        CheckModules(active, candidate, diagnostics);
        return diagnostics.Count == 0
            ? new ContentReloadResult(new ContentReloadCandidate(active, candidate), [])
            : new ContentReloadResult(null, diagnostics.ToImmutable());
    }

    private static void CheckEntities(ContentPack active, ContentPack candidate,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        var oldEntities = active.Entities.Definitions.ToDictionary(entity => entity.Id, StringComparer.Ordinal);
        var newEntities = candidate.Entities.Definitions.ToDictionary(entity => entity.Id, StringComparer.Ordinal);
        if (!oldEntities.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(newEntities.Keys))
        {
            diagnostics.Add(new("reload.entity_mismatch", "entities.json", "$.entities",
                "Stable entity IDs changed; a world migration is required."));
            return;
        }
        foreach (var (id, oldEntity) in oldEntities.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var next = newEntities[id];
            if (!string.Equals(oldEntity.Kind, next.Kind, StringComparison.Ordinal))
            {
                diagnostics.Add(new("reload.entity_mismatch", "entities.json", "$.entities",
                    $"Entity '{id}' changed kind from '{oldEntity.Kind}' to '{next.Kind}'."));
            }

            if (!oldEntity.Properties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(next.Properties.Keys)
                || !oldEntity.Links.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(next.Links.Keys))
            {
                diagnostics.Add(new("reload.entity_schema_mismatch", "entities.json", "$.entities",
                    $"Entity '{id}' changed property or link names; a world migration is required."));
            }
        }
    }

    private static void CheckModules(ContentPack active, ContentPack candidate,
        ImmutableArray<ContentDiagnostic>.Builder diagnostics)
    {
        var oldModules = active.Modules.IsDefaultOrEmpty ? [] : active.Modules;
        var newModules = candidate.Modules.IsDefaultOrEmpty ? [] : candidate.Modules;
        var oldById = oldModules.ToDictionary(module => module.Module.Id, StringComparer.Ordinal);
        var newById = newModules.ToDictionary(module => module.Module.Id, StringComparer.Ordinal);
        if (!oldById.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(newById.Keys))
        {
            diagnostics.Add(new("reload.modules_mismatch", "game.json", "$.modules",
                "Declared module IDs changed; extension state and services need migration."));
            return;
        }
        foreach (var (id, module) in oldById.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var next = newById[id];
            if (module.Module.GetType() != next.Module.GetType() || module.Content.GetType() != next.Content.GetType())
            {
                diagnostics.Add(new("reload.modules_mismatch", "game.json", "$.modules",
                    $"Module '{id}' changed implementation or content type; an extension migration is required."));
            }
        }
    }

    private static bool IsFingerprint(string? value) => value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static ContentReloadResult Failure(string code, string file, string path, string message) =>
        new(null, [new ContentDiagnostic(code, file, path, message)]);
}
