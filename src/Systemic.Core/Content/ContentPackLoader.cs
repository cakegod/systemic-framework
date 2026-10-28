using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Content;

/// <summary>Compiles core game files and delegates declared module files to registered modules.</summary>
public static partial class ContentPackLoader
{
    private const string GameSchemaId = "https://press-the-button.dev/systemic/game.schema.json";
    private const string EntitiesSchemaId = "https://press-the-button.dev/systemic/entities.schema.json";
    private const string RulesSchemaId = "https://press-the-button.dev/systemic/rules.schema.json";
    private static readonly string s_gameSchema = ReadSchema("Systemic.Core.Schemas.game.schema.json");
    private static readonly string s_entitiesSchema = ReadSchema("Systemic.Core.Schemas.entities.schema.json");
    private static readonly string s_rulesSchema = ReadSchema("Systemic.Core.Schemas.rules.schema.json");
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
    };

    public static ContentPack Load(string rootDirectory, ContentModuleRegistry? moduleRegistry = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        moduleRegistry ??= new ContentModuleRegistry();
        var root = Path.GetFullPath(rootDirectory);
        if (!Directory.Exists(root))
        {
            throw new ContentPackException($"Content pack directory does not exist: {root}");
        }

        root = new DirectoryInfo(root).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? root;
        root = Path.GetFullPath(root);
        var fingerprint = new ContentFingerprintBuilder(root);
        var diagnostics = new List<ContentDiagnostic>();
        var game = TryCompileAt(root, "game.json", "$", diagnostics, () =>
        {
            var file = Read<GameFile>(ResolveContentPath(root, "game.json"), "game.json", GameSchemaId, s_gameSchema, fingerprint);
            Require(file.SchemaVersion is 2 or 3,
                $"game.json schemaVersion must be 2 or 3; found {file.SchemaVersion}.", "$.schemaVersion");
            Require(!string.IsNullOrWhiteSpace(file.Name), "game.name is required.", "$.name");
            return file;
        }) ?? throw new ContentPackException(diagnostics);

        var rulesFile = TryCompileAt(root, game.Rules!, "$.rules", diagnostics,
            () => Read<RulesFile>(ResolveContentPath(root, game.Rules!), game.Rules!, RulesSchemaId, s_rulesSchema, fingerprint));
        byte[] entitiesBytes = [];
        var tuningDocuments = ImmutableDictionary<string, TuningDocument>.Empty;
        var entitiesFile = TryCompileAt(root, game.Entities!, "$.entities", diagnostics,
            () =>
            {
                var file = Read<EntitiesFile>(ResolveContentPath(root, game.Entities!), game.Entities!,
                    EntitiesSchemaId, s_entitiesSchema, fingerprint, out entitiesBytes);
                tuningDocuments = moduleRegistry.CompileTunings(entitiesBytes, game.Entities!);
                return file;
            });
        var entities = entitiesFile is null
            ? null
            : TryCompileAt(root, game.Entities!, "$.entities", diagnostics, () => CompileEntities(entitiesFile, game.Entities!));
        var rules = rulesFile is null
            ? null
            : TryCompileAt(root, game.Rules!, "$.rules", diagnostics, () => CompileRules(rulesFile, entities));

        if (diagnostics.Count > 0)
        {
            throw new ContentPackException(diagnostics);
        }

        var manifest = new GameManifest(game.Id!, game.Name!, game.TickRateHz, game.MaxCatchUpSteps);
        var references = new ContentReferenceCatalog();
        foreach (var entity in entities!.Definitions)
        {
            references.Add("entity", entity.Id, game.Entities!);
        }

        var moduleContext = new ContentModuleContext(root, manifest, entities, rules!, references,
            game.Entities, game.Rules)
        { Fingerprint = fingerprint };
        var modules = moduleRegistry.CompileDeclared(moduleContext, game.Modules);
        references.ValidateRequired();
        return new ContentPack(root, manifest, rules!, entities, modules)
        {
            ContentFingerprint = fingerprint.Finish(),
            TuningDocuments = tuningDocuments,
        };
    }

    private static T? TryCompileAt<T>(
        string root,
        string sourceFile,
        string fallbackPath,
        List<ContentDiagnostic> diagnostics,
        Func<T> compile)
        where T : class
    {
        try
        {
            return compile();
        }
        catch (ContentPackException exception)
        {
            if (!exception.Diagnostics.IsEmpty)
            {
                diagnostics.AddRange(exception.Diagnostics.Select(diagnostic => diagnostic with
                {
                    File = DiagnosticFile(root, diagnostic.File),
                }));
            }
            else
            {
                diagnostics.Add(new ContentDiagnostic("content.invalid", DiagnosticFile(root, sourceFile),
                    exception.FieldPath ?? fallbackPath, exception.Message));
            }
        }
        catch (ContentTuningException exception)
        {
            diagnostics.AddRange(exception.Diagnostics.Select(diagnostic => new ContentDiagnostic(
                diagnostic.Code, DiagnosticFile(root, diagnostic.File), diagnostic.Path,
                diagnostic.Message, diagnostic.Line, diagnostic.Column)));
        }
        catch (JsonException exception)
        {
            diagnostics.Add(new ContentDiagnostic("content.invalid_json", DiagnosticFile(root, sourceFile),
                exception.Path ?? fallbackPath, exception.Message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(new ContentDiagnostic("content.io_error", DiagnosticFile(root, sourceFile),
                fallbackPath, exception.Message));
        }

        return null;
    }

    private static string DiagnosticFile(string root, string sourceFile)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(root, sourceFile));
            var relative = Path.GetRelativePath(root, path);
            return relative.StartsWith("..", StringComparison.Ordinal) ? sourceFile : relative;
        }
        catch (ArgumentException)
        {
            return sourceFile;
        }
    }

    private static CompiledRuleSet CompileRules(RulesFile file, CompiledEntities? entities)
    {
        var fragments = new Dictionary<string, RuleFragmentFile>(StringComparer.Ordinal);
        var fragmentPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceFragments = file.Fragments ?? [];
        for (var fragmentIndex = 0; fragmentIndex < sourceFragments.Length; fragmentIndex++)
        {
            var sourceFragment = sourceFragments[fragmentIndex];
            var fragmentPath = $"$.fragments[{fragmentIndex}]";
            var fragment = sourceFragment!;
            Require(fragment.UseFragments is not { Length: > 0 }, $"Rule fragment '{fragment.Id}' cannot reference another fragment.", $"{fragmentPath}.useFragments");
            Require(fragments.TryAdd(fragment.Id!, fragment), $"Duplicate rule fragment id '{fragment.Id}'.", $"{fragmentPath}.id");
            fragmentPaths.Add(fragment.Id!, fragmentPath);
        }

        var expandedRules = new List<RuleSource>();
        var usedFragmentIds = file.UseFragments ?? [];
        for (var useIndex = 0; useIndex < usedFragmentIds.Length; useIndex++)
        {
            var fragmentId = usedFragmentIds[useIndex];
            var usePath = $"$.useFragments[{useIndex}]";
            Require(fragments.TryGetValue(fragmentId, out var fragment), $"Unknown rule fragment '{fragmentId}'.", usePath);
            var fragmentRules = fragment!.Rules ?? throw new ContentPackException($"Rule fragment '{fragmentId}' requires a rules array.", $"{fragmentPaths[fragmentId]}.rules");
            for (var ruleIndex = 0; ruleIndex < fragmentRules.Length; ruleIndex++)
            {
                var sourceRule = fragmentRules[ruleIndex];
                var rulePath = $"{fragmentPaths[fragmentId]}.rules[{ruleIndex}]";
                var rule = sourceRule!;
                expandedRules.Add(new RuleSource(rule with { Id = $"{fragmentId}.{rule.Id}" },
                    $"{fragmentPaths[fragmentId]}.rules[{ruleIndex}]"));
            }
        }

        var localRules = file.Rules!;
        for (var ruleIndex = 0; ruleIndex < localRules.Length; ruleIndex++)
        {
            var rule = localRules[ruleIndex]!;
            expandedRules.Add(new RuleSource(rule, $"$.rules[{ruleIndex}]"));
        }

        Require(expandedRules.Count <= 16_384, "rules.json exceeds the 16,384-rule limit.", "$.rules");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rules = ImmutableArray.CreateBuilder<CompiledRule>(expandedRules.Count);
        foreach (var source in expandedRules)
        {
            var rule = source.Rule;
            var rulePath = source.Path;
            Require(ids.Add(rule.Id!), $"Duplicate rule id '{rule.Id}'.", $"{rulePath}.id");

            var sourceConditions = rule.When ?? [];
            var conditions = ImmutableArray.CreateBuilder<RuleCondition>(sourceConditions.Length);
            for (var conditionIndex = 0; conditionIndex < sourceConditions.Length; conditionIndex++)
            {
                var sourceCondition = sourceConditions[conditionIndex];
                var conditionPath = $"{rulePath}.when[{conditionIndex}]";
                var condition = sourceCondition!;
                switch (condition.Kind)
                {
                    case "hasTag":
                        var tagSubject = ParseSubject(condition.Subject, condition.Row, condition.FactRow,
                            $"Rule '{rule.Id}' condition", $"{conditionPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(condition.Tag), $"Rule '{rule.Id}' hasTag requires tag.", $"{conditionPath}.tag");
                        conditions.Add(RuleCondition.HasTag(tagSubject, condition.Tag!));
                        break;
                    case "propertyAtLeast":
                        var propertySubject = ParseSubject(condition.Subject, condition.Row, condition.FactRow,
                            $"Rule '{rule.Id}' condition", $"{conditionPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(condition.Property), $"Rule '{rule.Id}' propertyAtLeast requires property.", $"{conditionPath}.property");
                        Require(condition.Value is { } threshold && double.IsFinite(threshold), $"Rule '{rule.Id}' condition value must be finite.", $"{conditionPath}.value");
                        conditions.Add(RuleCondition.PropertyAtLeast(propertySubject, condition.Property!, condition.Value!.Value));
                        break;
                    case "expression":
                        Require(!string.IsNullOrWhiteSpace(condition.Expression),
                            $"Rule '{rule.Id}' expression condition requires expression.", $"{conditionPath}.expression");
                        CompiledExpression formula;
                        try
                        {
                            formula = ExpressionCompiler.Compile(condition.Expression!);
                        }
                        catch (ExpressionCompileException exception)
                        {
                            throw new ContentPackException(
                                $"Rule '{rule.Id}' expression is invalid: {exception.Message}", $"{conditionPath}.expression");
                        }

                        Require(formula.ResultKind == ExpressionResultKind.Boolean,
                            $"Rule '{rule.Id}' condition expression must produce a boolean.", $"{conditionPath}.expression");
                        conditions.Add(RuleCondition.FromExpression(formula));
                        break;
                    case "chance":
                        Require(condition.Probability is { } probability && double.IsFinite(probability)
                            && probability is >= 0 and <= 1,
                            $"Rule '{rule.Id}' chance probability must be between 0 and 1.",
                            $"{conditionPath}.probability");
                        conditions.Add(RuleCondition.Chance(condition.Probability!.Value));
                        break;
                    default:
                        throw new ContentPackException($"Rule '{rule.Id}' uses unknown condition '{condition.Kind}'.", $"{conditionPath}.kind");
                }
            }

            var sourceEffects = rule.Effects ?? throw new ContentPackException($"Rule '{rule.Id}' requires an effects array.", $"{rulePath}.effects");
            var effects = ImmutableArray.CreateBuilder<RuleEffect>(sourceEffects.Length);
            for (var effectIndex = 0; effectIndex < sourceEffects.Length; effectIndex++)
            {
                var sourceEffect = sourceEffects[effectIndex];
                var effectPath = $"{rulePath}.effects[{effectIndex}]";
                var effect = sourceEffect!;
                switch (effect.Kind)
                {
                    case "adjustProperty":
                        var adjustSubject = ParseSubject(effect.Subject, effect.Row, effect.FactRow,
                            $"Rule '{rule.Id}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(effect.Property), $"Rule '{rule.Id}' adjustProperty requires property.", $"{effectPath}.property");
                        Require(effect.Amount is { } amount && double.IsFinite(amount), $"Rule '{rule.Id}' effect amount must be finite.", $"{effectPath}.amount");
                        effects.Add(RuleEffect.AdjustProperty(adjustSubject, effect.Property!, effect.Amount!.Value));
                        break;
                    case "setModifier":
                        var modifierSubject = ParseSubject(effect.Subject, effect.Row, effect.FactRow,
                            $"Rule '{rule.Id}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(effect.Property), $"Rule '{rule.Id}' setModifier requires property.", $"{effectPath}.property");
                        Require(effect.Operation is not null, $"Rule '{rule.Id}' setModifier requires operation.", $"{effectPath}.operation");
                        Require(effect.Value is { } modifierValue && double.IsFinite(modifierValue),
                            $"Rule '{rule.Id}' setModifier value must be finite.", $"{effectPath}.value");
                        var operation = effect.Operation switch
                        {
                            "add" => ModifierOperation.Add,
                            "multiply" => ModifierOperation.Multiply,
                            "set" => ModifierOperation.Set,
                            _ => throw new ContentPackException(
                                $"Rule '{rule.Id}' has unknown modifier operation '{effect.Operation}'.", $"{effectPath}.operation"),
                        };
                        var stage = effect.Stage switch
                        {
                            null or "standard" => ModifierStage.Standard,
                            "foundation" => ModifierStage.Foundation,
                            "additive" => ModifierStage.Additive,
                            "multiplicative" => ModifierStage.Multiplicative,
                            "override" => ModifierStage.Override,
                            "final" => ModifierStage.Final,
                            _ => throw new ContentPackException(
                                $"Rule '{rule.Id}' has unknown modifier stage '{effect.Stage}'.", $"{effectPath}.stage"),
                        };
                        var durationTicks = effect.DurationTicks ?? -1;
                        Require(durationTicks is -1 or >= 1 and <= 1_000_000,
                            $"Rule '{rule.Id}' modifier durationTicks must be between 1 and 1,000,000 when specified.",
                            $"{effectPath}.durationTicks");
                        effects.Add(RuleEffect.SetModifier(modifierSubject, effect.Property!, operation,
                            effect.Value!.Value, stage, durationTicks));
                        break;
                    case "addTag":
                        var tagSubject = ParseSubject(effect.Subject, effect.Row, effect.FactRow,
                            $"Rule '{rule.Id}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(effect.Tag), $"Rule '{rule.Id}' addTag requires tag.", $"{effectPath}.tag");
                        effects.Add(RuleEffect.AddTag(tagSubject, effect.Tag!));
                        break;
                    case "emitEvent":
                        Require(effect.Event is not null && StableIdRegex().IsMatch(effect.Event), $"Rule '{rule.Id}' emitEvent requires a stable event ID.", $"{effectPath}.event");
                        effects.Add(RuleEffect.Emit(effect.Event!));
                        break;
                    default:
                        throw new ContentPackException($"Rule '{rule.Id}' uses unknown effect '{effect.Kind}'.", $"{effectPath}.kind");
                }
            }

            rules.Add(new CompiledRule(rule.Id!, rule.On!, rule.Priority, conditions.MoveToImmutable(), effects.MoveToImmutable()));
        }

        var sourceSchedules = file.Schedules ?? [];
        var entityIds = entities?.Definitions.Select(definition => definition.Id)
            .ToHashSet(StringComparer.Ordinal);
        var scheduleIds = new HashSet<string>(StringComparer.Ordinal);
        var schedules = ImmutableArray.CreateBuilder<RuleScheduleDefinition>(sourceSchedules.Length);
        for (var index = 0; index < sourceSchedules.Length; index++)
        {
            var schedulePath = $"$.schedules[{index}]";
            var schedule = sourceSchedules[index]!;
            Require(scheduleIds.Add(schedule.Id!), $"Duplicate schedule id '{schedule.Id}'.", $"{schedulePath}.id");
            Require(schedule.OffsetTicks < schedule.EveryTicks,
                $"Schedule '{schedule.Id}' offsetTicks must be less than everyTicks.",
                $"{schedulePath}.offsetTicks");
            Require(double.IsFinite(schedule.Amount), $"Schedule '{schedule.Id}' amount must be finite.",
                $"{schedulePath}.amount");
            ValidateScheduledEntity(schedule.SourceEntityId, "sourceEntityId");
            ValidateScheduledEntity(schedule.TargetEntityId, "targetEntityId");
            schedules.Add(new RuleScheduleDefinition(schedule.Id!, schedule.Event!, schedule.EveryTicks,
                schedule.OffsetTicks, schedule.SourceEntityId, schedule.TargetEntityId, schedule.Amount));

            void ValidateScheduledEntity(string? entityId, string field)
            {
                if (entityId is null)
                {
                    return;
                }

                if (entityIds is not null)
                {
                    Require(entityIds.Contains(entityId),
                        $"Schedule '{schedule.Id}' references unknown entity '{entityId}'.", $"{schedulePath}.{field}");
                }
            }
        }

        return new CompiledRuleSet(rules.MoveToImmutable(), schedules.MoveToImmutable());
    }

    private static CompiledEntities CompileEntities(EntitiesFile file, string sourceFile)
    {
        var sourceDefinitions = file.Entities!;
        var sourceArchetypes = file.Archetypes ?? [];
        var archetypes = new Dictionary<string, EntityDefinitionFile>(sourceArchetypes.Length, StringComparer.Ordinal);
        var archetypeFieldSources = new Dictionary<string, ContentFieldSource?>(sourceArchetypes.Length, StringComparer.Ordinal);
        var archetypeIndex = 0;
        foreach (var archetype in sourceArchetypes)
        {
            var sourceIndex = archetypeIndex++;
            var template = archetype!;
            Require(template.Archetype is null, $"Entity archetype '{template.Id}' cannot reference another archetype.");
            Require(template.Kind is "actor" or "product" or "resource" or "prop", $"Entity archetype '{template.Id}' has an invalid kind '{template.Kind}'.");
            Require(archetypes.TryAdd(template.Id!, new EntityDefinitionFile(template.Id!,
                Kind: template.Kind,
                Tags: template.Tags,
                Capabilities: template.Capabilities,
                Properties: template.Properties,
                Links: template.Links,
                VisualRecipeId: template.VisualRecipeId,
                Bounds: template.Bounds)), $"Duplicate entity archetype id '{template.Id}'.");
            archetypeFieldSources.Add(template.Id!, template.VisualRecipeId is null
                ? null
                : new ContentFieldSource(sourceFile, $"$.archetypes[{sourceIndex}].visualRecipeId"));
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var definitions = ImmutableArray.CreateBuilder<EntityDefinition>(sourceDefinitions.Length);
        var spatialValidation = new SpatialIndex(World.DefaultSpatialCellSize);
        var runtimeId = 0;
        foreach (var source in sourceDefinitions)
        {
            runtimeId++;
            var definition = source!;
            var visualRecipeSource = definition.VisualRecipeId is null
                ? null
                : new ContentFieldSource(sourceFile, $"$.entities[{runtimeId - 1}].visualRecipeId");
            if (definition.Archetype is { } archetypeId)
            {
                Require(archetypes.TryGetValue(archetypeId, out var archetype), $"Entity '{definition.Id}' references unknown archetype '{archetypeId}'.");
                if (definition.VisualRecipeId is null)
                {
                    visualRecipeSource = archetypeFieldSources.GetValueOrDefault(archetypeId);
                }

                definition = MergeEntityDefinition(archetype!, definition);
            }

            Require(ids.Add(definition.Id!), $"Duplicate entity id '{definition.Id}'.");
            Require(definition.Kind is "actor" or "product" or "resource" or "prop",
                $"Entity '{definition.Id}' has unknown kind '{definition.Kind}'.");

            if (definition.Archetype is not null)
            {
                Require((definition.Tags?.Length ?? 0) <= 128, $"Entity '{definition.Id}' exceeds the 128-tag limit.");
                Require((definition.Capabilities?.Length ?? 0) <= 128, $"Entity '{definition.Id}' exceeds the 128-capability limit.");
                Require((definition.Properties?.Count ?? 0) <= 256, $"Entity '{definition.Id}' exceeds the 256-property limit.");
                Require((definition.Links?.Count ?? 0) <= 128, $"Entity '{definition.Id}' exceeds the 128-link limit.");
            }

            var tags = ToImmutableNames(definition.Tags ?? []);
            var capabilities = ToImmutableNames(definition.Capabilities ?? []);
            var properties = ImmutableDictionary.CreateBuilder<string, double>(StringComparer.Ordinal);
            foreach (var (name, value) in definition.Properties ?? [])
            {
                Require(double.IsFinite(value), $"Entity '{definition.Id}' property '{name}' must be finite.");
                properties.Add(name, value);
            }

            var links = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var (name, targetId) in definition.Links ?? [])
            {
                links.Add(name, targetId!);
            }

            Bounds2? bounds = null;
            if (definition.Bounds is { } spatial)
            {
                var value = new Bounds2(spatial.X, spatial.Y, spatial.HalfWidth, spatial.HalfHeight);
                Require(value.IsValid, $"Entity '{definition.Id}' has invalid spatial bounds.");
                try
                {
                    spatialValidation.Upsert(runtimeId, value);
                }
                catch (ArgumentOutOfRangeException exception)
                {
                    throw new ContentPackException($"Entity '{definition.Id}' exceeds the default spatial index limits: {exception.Message}", exception);
                }

                bounds = value;
            }

            var fieldSources = visualRecipeSource is { } visualRecipeFieldSource
                ? ImmutableDictionary<string, ContentFieldSource>.Empty.Add("visualRecipeId", visualRecipeFieldSource)
                : [];
            definitions.Add(new EntityDefinition(definition.Id!, definition.Kind!, tags, capabilities,
                properties.ToImmutable(), links.ToImmutable(), definition.VisualRecipeId, bounds)
            {
                FieldSources = fieldSources
            });
        }

        foreach (var definition in definitions)
        {
            foreach (var targetId in definition.Links.Values)
            {
                Require(ids.Contains(targetId), $"Entity '{definition.Id}' links to missing entity '{targetId}'.");
            }
        }

        return new CompiledEntities(definitions.MoveToImmutable());
    }

    private static EntityDefinitionFile MergeEntityDefinition(
        EntityDefinitionFile archetype,
        EntityDefinitionFile instance)
    {
        var removedTags = (instance.RemoveTags ?? []).ToHashSet(StringComparer.Ordinal);
        var removedCapabilities = (instance.RemoveCapabilities ?? []).ToHashSet(StringComparer.Ordinal);
        var tags = MergeNames(archetype.Tags, instance.Tags, removedTags);
        var capabilities = MergeNames(archetype.Capabilities, instance.Capabilities,
            removedCapabilities);
        var properties = MergeMap(archetype.Properties, instance.Properties);
        var links = MergeMap(archetype.Links, instance.Links);

        return instance with
        {
            Kind = instance.Kind ?? archetype.Kind,
            Tags = tags,
            Capabilities = capabilities,
            Properties = properties,
            Links = links,
            VisualRecipeId = instance.VisualRecipeId ?? archetype.VisualRecipeId,
            Bounds = instance.Bounds ?? archetype.Bounds,
            RemoveTags = null,
            RemoveCapabilities = null,
        };
    }

    private static string[] MergeNames(
        string[]? inherited,
        string[]? added,
        HashSet<string> removed)
    {
        var merged = new List<string>((inherited?.Length ?? 0) + (added?.Length ?? 0));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in inherited ?? [])
        {
            if (seen.Add(name))
            {
                merged.Add(name);
            }
        }

        foreach (var name in added ?? [])
        {
            if (seen.Add(name))
            {
                merged.Add(name);
            }
        }

        return [.. merged.Where(name => !removed.Contains(name))];
    }

    private static Dictionary<string, TValue> MergeMap<TValue>(
        Dictionary<string, TValue>? inherited,
        Dictionary<string, TValue>? overrides)
    {
        var merged = inherited is null
            ? new Dictionary<string, TValue>(StringComparer.Ordinal)
            : new Dictionary<string, TValue>(inherited, StringComparer.Ordinal);
        if (overrides is not null)
        {
            foreach (var (name, value) in overrides)
            {
                merged[name] = value;
            }
        }

        return merged;
    }

    private static ImmutableArray<string> ToImmutableNames(string[] names) => [.. names];

    // A subject is one of four forms: the fact's source, the fact's target, a fixed population row,
    // or the population row the fact is about. Only one form may be written; guessing between them
    // would make an authoring mistake silently address the wrong thing.
    private static RuleSubject ParseSubject(string? subject, RowFile? row, string? factRow,
        string context, string? fieldPath = null)
    {
        ContentPackException Error(string message) => fieldPath is null
            ? new ContentPackException($"{context} {message}")
            : new ContentPackException($"{context} {message}", fieldPath);

        var forms = (subject is null ? 0 : 1) + (row is null ? 0 : 1) + (factRow is null ? 0 : 1);
        if (forms > 1)
        {
            throw Error("must use only one of 'subject', 'row' or 'factRow'.");
        }

        if (row is not null)
        {
            if (string.IsNullOrWhiteSpace(row.Population))
            {
                throw Error("row subject requires a population.");
            }

            if (row.Slot < 0)
            {
                throw Error("row subject slot must be non-negative.");
            }

            return RuleSubject.Row(row.Population, row.Slot);
        }

        if (factRow is not null)
        {
            if (string.IsNullOrWhiteSpace(factRow))
            {
                throw Error("factRow subject requires a population id.");
            }

            return RuleSubject.FactRow(factRow);
        }

        return subject switch
        {
            "source" => RuleSubject.Source,
            "target" => RuleSubject.Target,
            _ => throw Error("subject must be 'source', 'target', or a 'row' / 'factRow' population."),
        };
    }

    private static string ResolveContentPath(string root, string relativePath)
    {
        Require(!string.IsNullOrWhiteSpace(relativePath) && !Path.IsPathRooted(relativePath),
            "Content file paths must be relative to the pack root.");
        return ContentPathResolver.ResolveWithinRoot(root, relativePath, "Content path", "Content file");
    }

    private static T Read<T>(string path, string displayPath, string schemaKey, string schemaText, ContentFingerprintBuilder fingerprint)
    {
        var bytes = ContentFileReader.ReadValidatedBytes(path, displayPath, schemaKey, schemaText, fingerprint);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)!;
    }

    private static T Read<T>(string path, string displayPath, string schemaKey, string schemaText,
        ContentFingerprintBuilder fingerprint, out byte[] bytes)
    {
        bytes = ContentFileReader.ReadValidatedBytes(path, displayPath, schemaKey, schemaText, fingerprint);
        return JsonSerializer.Deserialize<T>(bytes, JsonOptions)!;
    }

    private static string ReadSchema(string resourceName)
    {
        using var stream = typeof(ContentPackLoader).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded content schema '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void Require(bool condition, string message, string? fieldPath = null)
    {
        if (!condition)
        {
            if (fieldPath is not null)
            {
                throw new ContentPackException(message, fieldPath);
            }

            throw new ContentPackException(message);
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex StableIdRegex();


    private sealed record GameFile(
        int SchemaVersion,
        string? Id,
        string? Name,
        int TickRateHz,
        int MaxCatchUpSteps,
        string? Rules,
        string? Entities,
        Dictionary<string, JsonElement>? Modules = null);

    private sealed record RulesFile(
        int SchemaVersion,
        RuleFile[]? Rules = null,
        RuleFragmentFile[]? Fragments = null,
        string[]? UseFragments = null,
        ScheduleFile[]? Schedules = null);

    private sealed record RuleSource(RuleFile Rule, string Path);
    private sealed record RuleFragmentFile(string Id, RuleFile[]? Rules = null, string[]? UseFragments = null);
    private sealed record ScheduleFile(
        string Id,
        string Event,
        int EveryTicks,
        int OffsetTicks = 0,
        string? SourceEntityId = null,
        string? TargetEntityId = null,
        double Amount = 0);
    private sealed record RuleFile(string Id, string On, int Priority, ConditionFile[]? When = null, EffectFile[]? Effects = null);
    private sealed record ConditionFile(
        string Kind,
        string? Subject = null,
        string? Tag = null,
        string? Property = null,
        double? Value = null,
        string? Expression = null,
        double? Probability = null,
        RowFile? Row = null,
        string? FactRow = null);
    private sealed record EffectFile(
        string Kind,
        string? Subject = null,
        string? Property = null,
        double? Amount = null,
        string? Tag = null,
        string? Event = null,
        string? Operation = null,
        double? Value = null,
        string? Stage = null,
        int? DurationTicks = null,
        RowFile? Row = null,
        string? FactRow = null);
    private sealed record RowFile(string Population, int Slot = 0);
    private sealed record EntitiesFile(
        int SchemaVersion,
        EntityDefinitionFile[]? Entities = null,
        EntityArchetypeFile[]? Archetypes = null);
    private sealed record EntityArchetypeFile(
        string Id,
        string? Kind = null,
        string[]? Tags = null,
        string[]? Capabilities = null,
        Dictionary<string, double>? Properties = null,
        Dictionary<string, string>? Links = null,
        string? VisualRecipeId = null,
        BoundsFile? Bounds = null,
        string? Archetype = null);
    private sealed record EntityDefinitionFile(
        string Id,
        string? Kind = null,
        string? Archetype = null,
        string[]? Tags = null,
        string[]? Capabilities = null,
        Dictionary<string, double>? Properties = null,
        Dictionary<string, string>? Links = null,
        string? VisualRecipeId = null,
        BoundsFile? Bounds = null,
        string[]? RemoveTags = null,
        string[]? RemoveCapabilities = null);
    private sealed record BoundsFile(float X, float Y, float HalfWidth, float HalfHeight);
}
