using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Systemic.Content.Generated;
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
        byte[] gameBytes = [];
        if (!TryCompile(root, "game.json", "$", diagnostics, () =>
            {
                gameBytes = ReadGameBytes(ResolveContentPath(root, "game.json"), "game.json",
                    GameSchemaId, s_gameSchema, fingerprint);
            }))
        {
            throw new ContentPackException(diagnostics);
        }

        using var gameDocument = gameBytes.Length > 0
            ? Corvus.Text.Json.ParsedJsonDocument<GameContentFile>.Parse(gameBytes)
            : null;
        var game = gameDocument!.RootElement;
        if (!TryCompile(root, "game.json", "$", diagnostics, () =>
            {
                Require((int)game.SchemaVersion is 2 or 3,
                    $"game.json schemaVersion must be 2 or 3; found {(int)game.SchemaVersion}.", "$.schemaVersion");
                Require(!string.IsNullOrWhiteSpace(game.Name.GetString()), "game.name is required.", "$.name");
            }))
        {
            throw new ContentPackException(diagnostics);
        }

        var gameRules = game.Rules.GetString()!;
        var gameEntities = game.Entities.GetString()!;
        var gameModules = ReadModuleDeclarations(gameBytes);

        byte[] rulesBytes = [];
        var rulesRead = TryCompile(root, gameRules, "$.rules", diagnostics, () =>
        {
            rulesBytes = ReadRulesBytes(ResolveContentPath(root, gameRules), gameRules, RulesSchemaId,
                s_rulesSchema, fingerprint);
        });
        using var rulesDocument = rulesRead && rulesBytes.Length > 0
            ? Corvus.Text.Json.ParsedJsonDocument<RulesContentFile>.Parse(rulesBytes)
            : null;
        var rulesFile = rulesDocument?.RootElement;
        byte[] entitiesBytes = [];
        var tuningDocuments = ImmutableDictionary<string, TuningDocument>.Empty;
        var entitiesFile = TryCompileAt(root, gameEntities, "$.entities", diagnostics,
            () =>
            {
                return Read<EntitiesFile>(ResolveContentPath(root, gameEntities), gameEntities,
                    EntitiesSchemaId, s_entitiesSchema, fingerprint, out entitiesBytes);
            });
        var entities = entitiesFile is null
            ? null
            : TryCompileAt(root, gameEntities, "$.entities", diagnostics, () => CompileEntities(entitiesFile, gameEntities));
        // Tunings validate the merged entity, so a value or tag inherited from an archetype counts.
        if (entitiesFile is not null && entities is not null && moduleRegistry.HasTuningSchemas)
        {
            TryCompile(root, gameEntities, "$.entities", diagnostics,
                () => tuningDocuments = moduleRegistry.CompileTunings(entitiesBytes, gameEntities, entities));
        }
        var rules = rulesFile is null
            ? null
            : TryCompileAt(root, gameRules, "$.rules", diagnostics, () => CompileRules(rulesFile.Value, entities));

        if (diagnostics.Count > 0)
        {
            throw new ContentPackException(diagnostics);
        }

        var manifest = new GameManifest((string)game.IdValue, game.Name.GetString()!, (int)game.TickRateHz,
            (int)game.MaxCatchUpSteps);
        var references = new ContentReferenceCatalog();
        foreach (var entity in entities!.Definitions)
        {
            references.Add("entity", entity.Id, gameEntities);
        }

        var moduleContext = new ContentModuleContext(root, manifest, entities, rules!, references,
            gameEntities, gameRules)
        { Fingerprint = fingerprint };
        var modules = moduleRegistry.CompileDeclared(moduleContext, gameModules);
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
        T? result = null;
        return TryCompile(root, sourceFile, fallbackPath, diagnostics, () => result = compile())
            ? result
            : null;
    }

    private static bool TryCompile(
        string root,
        string sourceFile,
        string fallbackPath,
        List<ContentDiagnostic> diagnostics,
        Action compile)
    {
        try
        {
            compile();
            return true;
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

        return false;
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

    private static CompiledRuleSet CompileRules(RulesContentFile file, CompiledEntities? entities)
    {
        var fragments = new Dictionary<string, RulesContentFile.Fragment>(StringComparer.Ordinal);
        var fragmentPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        if (file.Fragments is { } sourceFragments)
        {
            for (var fragmentIndex = 0; fragmentIndex < sourceFragments.GetArrayLength(); fragmentIndex++)
            {
                var fragment = sourceFragments[fragmentIndex];
                var fragmentId = (string)fragment.IdValue;
                var fragmentPath = $"$.fragments[{fragmentIndex}]";
                Require(fragment.UseFragments is not { } inherited || inherited.GetArrayLength() == 0,
                    $"Rule fragment '{fragmentId}' cannot reference another fragment.", $"{fragmentPath}.useFragments");
                Require(fragments.TryAdd(fragmentId, fragment), $"Duplicate rule fragment id '{fragmentId}'.", $"{fragmentPath}.id");
                fragmentPaths.Add(fragmentId, fragmentPath);
            }
        }

        var factDefinitions = new Dictionary<string, FactDefinition>(StringComparer.Ordinal);
        var facts = ImmutableArray.CreateBuilder<FactDefinition>();
        var factsDeclared = file.Facts is not null;
        if (file.Facts is { } sourceFacts)
        {
            for (var factIndex = 0; factIndex < sourceFacts.GetArrayLength(); factIndex++)
            {
                var fact = sourceFacts[factIndex];
                var factPath = $"$.facts[{factIndex}]";
                var factId = (string)fact.IdValue;
                var fields = ImmutableArray.CreateBuilder<FactFieldDefinition>();
                var fieldKeys = new HashSet<string>(StringComparer.Ordinal);
                if (fact.Payload is { } sourceFields)
                {
                    for (var fieldIndex = 0; fieldIndex < sourceFields.GetArrayLength(); fieldIndex++)
                    {
                        var field = sourceFields[fieldIndex];
                        var fieldPath = $"{factPath}.payload[{fieldIndex}]";
                        var key = field.Key.GetString();
                        var kindName = field.Kind.GetString();
                        Require(!string.IsNullOrWhiteSpace(key)
                            && key.Length <= SimulationFactPayload.MaximumKeyLength,
                            $"Fact '{factId}' payload key must be 1-{SimulationFactPayload.MaximumKeyLength} characters.",
                            $"{fieldPath}.key");
                        Require(fieldKeys.Add(key!),
                            $"Fact '{factId}' declares payload key '{key}' more than once.",
                            $"{fieldPath}.key");
                        var kind = kindName switch
                        {
                            "integer" => SimulationFactValueKind.Integer,
                            "number" => SimulationFactValueKind.Number,
                            "text" => SimulationFactValueKind.Text,
                            "boolean" => SimulationFactValueKind.Boolean,
                            _ => throw new ContentPackException(
                                $"Fact '{factId}' has unknown payload kind '{kindName}'.", $"{fieldPath}.kind"),
                        };
                        fields.Add(new FactFieldDefinition(key!, kind));
                    }
                }

                var definition = new FactDefinition(factId, fields.ToImmutable());
                Require(factDefinitions.TryAdd(factId, definition),
                    $"Duplicate fact id '{factId}'.", $"{factPath}.id");
                facts.Add(definition);
            }
        }

        var expandedRules = new List<(RulesContentFile.Rule Rule, string Path, string? Prefix)>();
        if (file.UseFragments is { } usedFragmentIds)
        {
            for (var useIndex = 0; useIndex < usedFragmentIds.GetArrayLength(); useIndex++)
            {
                var fragmentId = (string)usedFragmentIds[useIndex];
                var usePath = $"$.useFragments[{useIndex}]";
                Require(fragments.TryGetValue(fragmentId, out var fragment), $"Unknown rule fragment '{fragmentId}'.", usePath);
                var fragmentRules = fragment.Rules;
                for (var ruleIndex = 0; ruleIndex < fragmentRules.GetArrayLength(); ruleIndex++)
                {
                    expandedRules.Add((fragmentRules[ruleIndex], $"{fragmentPaths[fragmentId]}.rules[{ruleIndex}]", fragmentId));
                }
            }
        }

        var localRules = file.Rules;
        for (var ruleIndex = 0; ruleIndex < localRules.GetArrayLength(); ruleIndex++)
        {
            expandedRules.Add((localRules[ruleIndex], $"$.rules[{ruleIndex}]", null));
        }

        Require(expandedRules.Count <= 16_384, "rules.json exceeds the 16,384-rule limit.", "$.rules");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var rules = ImmutableArray.CreateBuilder<CompiledRule>(expandedRules.Count);
        var spawnTemplates = new Dictionary<string, SpawnTemplate>(StringComparer.Ordinal);
        var bundles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (file.Bundles is { } sourceBundles)
        {
            for (var bundleIndex = 0; bundleIndex < sourceBundles.GetArrayLength(); bundleIndex++)
            {
                var bundle = sourceBundles[bundleIndex];
                var bundlePath = $"$.bundles[{bundleIndex}]";
                var bundleId = (string)bundle.IdValue;
                var enabledByTag = (string)bundle.EnabledByTag;
                Require(!string.IsNullOrWhiteSpace(enabledByTag),
                    $"Bundle '{bundleId}' requires enabledByTag.", $"{bundlePath}.enabledByTag");
                Require(bundles.TryAdd(bundleId, enabledByTag!),
                    $"Duplicate bundle id '{bundleId}'.", $"{bundlePath}.id");
            }
        }
        foreach (var source in expandedRules)
        {
            var rule = source.Rule;
            var rulePath = source.Path; var ruleId = source.Prefix is { } prefix ? $"{prefix}.{(string)rule.IdValue}" : (string)rule.IdValue;
            Require(ids.Add(ruleId), $"Duplicate rule id '{ruleId}'.", $"{rulePath}.id");
            var eventType = (string)rule.On;
            var bundleName = rule.Bundle?.GetString();
            if (bundleName is not null)
            {
                Require(bundles.ContainsKey(bundleName),
                    $"Rule '{ruleId}' references unknown bundle '{bundleName}'.", $"{rulePath}.bundle");
            }

            var cooldownTicks = rule.CooldownTicks is { } cooldownValue ? (int)cooldownValue : 0;
            if (factsDeclared)
            {
                Require(factDefinitions.ContainsKey(eventType),
                    $"Rule '{ruleId}' listens for undeclared fact '{eventType}'.", $"{rulePath}.on");
            }

            var conditions = ImmutableArray.CreateBuilder<RuleCondition>();
            if (rule.When is { } sourceConditions)
            {
                for (var conditionIndex = 0; conditionIndex < sourceConditions.GetArrayLength(); conditionIndex++)
                {
                    var condition = sourceConditions[conditionIndex];
                    var conditionPath = $"{rulePath}.when[{conditionIndex}]";
                    var conditionKind = condition.Kind.GetString();
                    switch (conditionKind)
                    {
                        case "hasTag":
                            var tag = condition.Tag?.GetString();
                            var tagSubject = ParseSubject(condition.SubjectValue?.GetString(), condition.Row,
                                condition.FactRow?.GetString(), $"Rule '{ruleId}' condition", $"{conditionPath}.subject");
                            Require(!string.IsNullOrWhiteSpace(tag), $"Rule '{ruleId}' hasTag requires tag.", $"{conditionPath}.tag");
                            conditions.Add(RuleCondition.HasTag(tagSubject, tag!));
                            break;
                        case "propertyAtLeast":
                            var property = condition.Property?.GetString();
                            var threshold = condition.Value is { } thresholdValue ? (double)thresholdValue : (double?)null;
                            var propertySubject = ParseSubject(condition.SubjectValue?.GetString(), condition.Row,
                                condition.FactRow?.GetString(), $"Rule '{ruleId}' condition", $"{conditionPath}.subject");
                            Require(!string.IsNullOrWhiteSpace(property), $"Rule '{ruleId}' propertyAtLeast requires property.", $"{conditionPath}.property");
                            Require(threshold is { } finiteThreshold && double.IsFinite(finiteThreshold),
                                $"Rule '{ruleId}' condition value must be finite.", $"{conditionPath}.value");
                            conditions.Add(RuleCondition.PropertyAtLeast(propertySubject, property!, threshold.GetValueOrDefault()));
                            break;
                        case "expression":
                            var expression = condition.Expression?.GetString();
                            Require(!string.IsNullOrWhiteSpace(expression),
                                $"Rule '{ruleId}' expression condition requires expression.", $"{conditionPath}.expression");
                            CompiledExpression formula;
                            try
                            {
                                formula = ExpressionCompiler.Compile(expression!);
                            }
                            catch (ExpressionCompileException exception)
                            {
                                throw new ContentPackException(
                                    $"Rule '{ruleId}' expression is invalid: {exception.Message}", $"{conditionPath}.expression");
                            }

                            Require(formula.ResultKind == ExpressionResultKind.Boolean,
                                $"Rule '{ruleId}' condition expression must produce a boolean.", $"{conditionPath}.expression");
                            conditions.Add(RuleCondition.FromExpression(formula));
                            break;
                        case "chance":
                            var probability = condition.Probability is { } probabilityValue
                                ? (double)probabilityValue
                                : (double?)null;
                            Require(probability is { } finiteProbability && double.IsFinite(finiteProbability)
                                && finiteProbability is >= 0 and <= 1,
                                $"Rule '{ruleId}' chance probability must be between 0 and 1.",
                                $"{conditionPath}.probability");
                            conditions.Add(RuleCondition.Chance(probability.GetValueOrDefault()));
                            break;
                        case "query":
                            conditions.Add(RuleCondition.FromQuery(CompileQuery(ruleId, condition, conditionPath)));
                            break;
                        default:
                            throw new ContentPackException($"Rule '{ruleId}' uses unknown condition '{conditionKind}'.", $"{conditionPath}.kind");
                    }
                }
            }

            var sourceEffects = rule.Effects;
            var effects = ImmutableArray.CreateBuilder<RuleEffect>(sourceEffects.GetArrayLength());
            for (var effectIndex = 0; effectIndex < sourceEffects.GetArrayLength(); effectIndex++)
            {
                var effect = sourceEffects[effectIndex];
                var effectPath = $"{rulePath}.effects[{effectIndex}]";
                var effectKind = effect.Kind.GetString();
                switch (effectKind)
                {
                    case "adjustProperty":
                        var adjustProperty = effect.Property?.GetString();
                        var amount = effect.Amount is { } amountValue ? (double)amountValue : (double?)null;
                        var adjustSubject = ParseSubject(effect.SubjectValue?.GetString(), effect.Row,
                            effect.FactRow?.GetString(), $"Rule '{ruleId}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(adjustProperty),
                            $"Rule '{ruleId}' adjustProperty requires property.", $"{effectPath}.property");
                        Require(amount is { } finiteAmount && double.IsFinite(finiteAmount),
                            $"Rule '{ruleId}' effect amount must be finite.", $"{effectPath}.amount");
                        var quantityName = effect.Quantity?.GetString();
                        var quantity = quantityName switch
                        {
                            null or "add" => QuantityOperation.Add,
                            "set" => QuantityOperation.Set,
                            "multiply" => QuantityOperation.Multiply,
                            "min" => QuantityOperation.Min,
                            "max" => QuantityOperation.Max,
                            _ => throw new ContentPackException(
                                $"Rule '{ruleId}' has unknown quantity operation '{quantityName}'.",
                                $"{effectPath}.quantity"),
                        };
                        effects.Add(RuleEffect.AdjustProperty(adjustSubject, adjustProperty!, amount.GetValueOrDefault(), quantity));
                        break;
                    case "setModifier":
                        var modifierProperty = effect.Property?.GetString();
                        var modifierValue = effect.Value is { } value ? (double)value : (double?)null;
                        var operationName = effect.Operation?.GetString();
                        var stageName = effect.Stage?.GetString();
                        var durationTicks = effect.DurationTicks is { } ticks ? (int)ticks : -1;
                        var modifierSubject = ParseSubject(effect.SubjectValue?.GetString(), effect.Row,
                            effect.FactRow?.GetString(), $"Rule '{ruleId}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(modifierProperty),
                            $"Rule '{ruleId}' setModifier requires property.", $"{effectPath}.property");
                        Require(operationName is not null, $"Rule '{ruleId}' setModifier requires operation.", $"{effectPath}.operation");
                        Require(modifierValue is { } finiteModifierValue && double.IsFinite(finiteModifierValue),
                            $"Rule '{ruleId}' setModifier value must be finite.", $"{effectPath}.value");
                        var operation = operationName switch
                        {
                            "add" => ModifierOperation.Add,
                            "multiply" => ModifierOperation.Multiply,
                            "set" => ModifierOperation.Set,
                            "min" => ModifierOperation.Min,
                            "max" => ModifierOperation.Max,
                            _ => throw new ContentPackException(
                                $"Rule '{ruleId}' has unknown modifier operation '{operationName}'.", $"{effectPath}.operation"),
                        };
                        var stage = stageName switch
                        {
                            null or "standard" => ModifierStage.Standard,
                            "foundation" => ModifierStage.Foundation,
                            "additive" => ModifierStage.Additive,
                            "multiplicative" => ModifierStage.Multiplicative,
                            "override" => ModifierStage.Override,
                            "final" => ModifierStage.Final,
                            _ => throw new ContentPackException(
                                $"Rule '{ruleId}' has unknown modifier stage '{stageName}'.", $"{effectPath}.stage"),
                        };
                        Require(durationTicks is -1 or >= 1 and <= 1_000_000,
                            $"Rule '{ruleId}' modifier durationTicks must be between 1 and 1,000,000 when specified.",
                            $"{effectPath}.durationTicks");
                        var fromProperty = effect.FromProperty?.GetString();
                        Require(fromProperty is null || !string.IsNullOrWhiteSpace(fromProperty),
                            $"Rule '{ruleId}' setModifier fromProperty cannot be blank.",
                            $"{effectPath}.fromProperty");
                        effects.Add(fromProperty is null
                            ? RuleEffect.SetModifier(modifierSubject, modifierProperty!, operation,
                                modifierValue.GetValueOrDefault(), stage, durationTicks)
                            : RuleEffect.SetModifierFromProperty(modifierSubject, modifierProperty!, operation,
                                modifierValue.GetValueOrDefault(), fromProperty, stage, durationTicks));
                        break;
                    case "addTag":
                        var addedTag = effect.Tag?.GetString();
                        var tagSubject = ParseSubject(effect.SubjectValue?.GetString(), effect.Row,
                            effect.FactRow?.GetString(), $"Rule '{ruleId}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(addedTag), $"Rule '{ruleId}' addTag requires tag.", $"{effectPath}.tag");
                        effects.Add(RuleEffect.AddTag(tagSubject, addedTag!));
                        break;
                    case "removeTag":
                        var removedTag = effect.Tag?.GetString();
                        var removeTagSubject = ParseSubject(effect.SubjectValue?.GetString(), effect.Row,
                            effect.FactRow?.GetString(), $"Rule '{ruleId}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(removedTag),
                            $"Rule '{ruleId}' removeTag requires tag.", $"{effectPath}.tag");
                        effects.Add(RuleEffect.RemoveTag(removeTagSubject, removedTag!));
                        break;
                    case "despawn":
                        var despawnSubject = ParseSubject(effect.SubjectValue?.GetString(), effect.Row,
                            effect.FactRow?.GetString(), $"Rule '{ruleId}' effect", $"{effectPath}.subject");
                        effects.Add(RuleEffect.Despawn(despawnSubject));
                        break;
                    case "transfer":
                        var transferProperty = effect.Property?.GetString();
                        var transferTargetProperty = effect.TargetProperty?.GetString() ?? transferProperty;
                        var transferAmount = effect.Amount is { } transferAmountValue
                            ? (double)transferAmountValue
                            : (double?)null;
                        var transferSubject = ParseSubject(effect.SubjectValue?.GetString(), effect.Row,
                            effect.FactRow?.GetString(), $"Rule '{ruleId}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(transferProperty),
                            $"Rule '{ruleId}' transfer requires property.", $"{effectPath}.property");
                        Require(!string.IsNullOrWhiteSpace(transferTargetProperty),
                            $"Rule '{ruleId}' transfer requires targetProperty.", $"{effectPath}.targetProperty");
                        Require(transferAmount is { } finiteTransferAmount && double.IsFinite(finiteTransferAmount)
                            && finiteTransferAmount >= 0,
                            $"Rule '{ruleId}' transfer amount must be zero or more.", $"{effectPath}.amount");
                        effects.Add(RuleEffect.Transfer(transferSubject, transferProperty!,
                            transferTargetProperty!, transferAmount.GetValueOrDefault()));
                        break;
                    case "spawn":
                        var templateId = effect.Template?.GetString();
                        var spawnSubject = ParseSubject(effect.SubjectValue?.GetString(), effect.Row,
                            effect.FactRow?.GetString(), $"Rule '{ruleId}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(templateId),
                            $"Rule '{ruleId}' spawn requires template.", $"{effectPath}.template");
                        var templateDefinition = entities?.Definitions.FirstOrDefault(definition =>
                            string.Equals(definition.Id, templateId, StringComparison.Ordinal));
                        Require(templateDefinition is not null,
                            $"Rule '{ruleId}' spawn names unknown template '{templateId}'.",
                            $"{effectPath}.template");
                        var spawnTemplate = new SpawnTemplate(templateDefinition!.Id, templateDefinition.Tags,
                            templateDefinition.Capabilities, templateDefinition.Properties,
                            templateDefinition.SpatialBounds);
                        spawnTemplates[spawnTemplate.Id] = spawnTemplate;
                        effects.Add(RuleEffect.Spawn(spawnSubject, spawnTemplate));
                        break;
                    case "schedule":
                        var scheduledEvent = effect.Event?.GetString();
                        Require(scheduledEvent is not null && StableIdRegex().IsMatch(scheduledEvent),
                            $"Rule '{ruleId}' schedule requires a stable event ID.", $"{effectPath}.event");
                        var delayTicks = effect.DelayTicks is { } delay ? (int)delay : 0;
                        Require(delayTicks is >= 1 and <= 1_000_000,
                            $"Rule '{ruleId}' schedule delayTicks must be between 1 and 1,000,000.",
                            $"{effectPath}.delayTicks");
                        FactDefinition? scheduledFactDefinition = null;
                        if (factsDeclared)
                        {
                            Require(factDefinitions.TryGetValue(scheduledEvent!, out scheduledFactDefinition),
                                $"Rule '{ruleId}' schedules undeclared fact '{scheduledEvent}'.",
                                $"{effectPath}.event");
                        }

                        var scheduledPayload = effect.Payload is { } schedulePayloadSource
                            ? CompilePayload(schedulePayloadSource, $"{effectPath}.payload", scheduledFactDefinition)
                            : null;
                        effects.Add(RuleEffect.Schedule(scheduledEvent!, delayTicks, scheduledPayload));
                        break;
                    case "action":
                        var actionName = effect.Action?.GetString();
                        var actionSubject = ParseSubject(effect.SubjectValue?.GetString(), effect.Row,
                            effect.FactRow?.GetString(), $"Rule '{ruleId}' effect", $"{effectPath}.subject");
                        Require(!string.IsNullOrWhiteSpace(actionName),
                            $"Rule '{ruleId}' action requires a name.", $"{effectPath}.action");
                        effects.Add(RuleEffect.InvokeAction(actionName!, actionSubject));
                        break;
                    case "emitEvent":
                        var emittedEvent = effect.Event?.GetString();
                        Require(emittedEvent is not null && StableIdRegex().IsMatch(emittedEvent),
                            $"Rule '{ruleId}' emitEvent requires a stable event ID.", $"{effectPath}.event");
                        FactDefinition? emittedFact = null;
                        if (factsDeclared)
                        {
                            Require(emittedEvent is not null
                                && factDefinitions.TryGetValue(emittedEvent, out emittedFact),
                                $"Rule '{ruleId}' emits undeclared fact '{emittedEvent}'.", $"{effectPath}.event");
                        }

                        var emittedPayload = effect.Payload is { } sourcePayload
                            ? CompilePayload(sourcePayload, $"{effectPath}.payload", emittedFact)
                            : null;
                        effects.Add(RuleEffect.Emit(emittedEvent!, emittedPayload));
                        break;
                    default:
                        throw new ContentPackException($"Rule '{ruleId}' uses unknown effect '{effectKind}'.", $"{effectPath}.kind");
                }
            }

            rules.Add(new CompiledRule(ruleId, (string)rule.On, (int)rule.Priority,
                conditions.ToImmutable(), effects.ToImmutable())
            {
                BundleId = bundleName,
                CooldownTicks = cooldownTicks,
            });
        }

        var entityIds = entities?.Definitions.Select(definition => definition.Id)
            .ToHashSet(StringComparer.Ordinal);
        var scheduleIds = new HashSet<string>(StringComparer.Ordinal);
        var schedules = ImmutableArray.CreateBuilder<RuleScheduleDefinition>(
            file.Schedules is { } scheduled ? scheduled.GetArrayLength() : 0);
        if (file.Schedules is { } sourceSchedules)
        {
            for (var index = 0; index < sourceSchedules.GetArrayLength(); index++)
            {
                var schedule = sourceSchedules[index];
                var schedulePath = $"$.schedules[{index}]";
                var scheduleId = (string)schedule.IdValue;
                var scheduleEvent = (string)schedule.Event;
                var everyTicks = (int)schedule.EveryTicks;
                var offsetTicks = schedule.OffsetTicks is { } offset ? (int)offset : 0;
                var amount = schedule.Amount is { } scheduleAmount ? (double)scheduleAmount : 0;
                var sourceEntityId = schedule.SourceEntityId?.GetString();
                var targetEntityId = schedule.TargetEntityId?.GetString();
                Require(scheduleIds.Add(scheduleId), $"Duplicate schedule id '{scheduleId}'.", $"{schedulePath}.id");
                Require(offsetTicks < everyTicks,
                    $"Schedule '{scheduleId}' offsetTicks must be less than everyTicks.",
                    $"{schedulePath}.offsetTicks");
                Require(double.IsFinite(amount), $"Schedule '{scheduleId}' amount must be finite.",
                    $"{schedulePath}.amount");
                ValidateScheduledEntity(sourceEntityId, "sourceEntityId");
                ValidateScheduledEntity(targetEntityId, "targetEntityId");
                FactDefinition? scheduledFact = null;
                if (factsDeclared)
                {
                    Require(factDefinitions.TryGetValue(scheduleEvent, out scheduledFact),
                        $"Schedule '{scheduleId}' emits undeclared fact '{scheduleEvent}'.",
                        $"{schedulePath}.event");
                }

                var schedulePayload = schedule.Payload is { } sourcePayload
                    ? CompilePayload(sourcePayload, $"{schedulePath}.payload", scheduledFact)
                    : null;
                schedules.Add(new RuleScheduleDefinition(scheduleId, scheduleEvent, everyTicks,
                    offsetTicks, sourceEntityId, targetEntityId, amount)
                { Payload = schedulePayload });

                void ValidateScheduledEntity(string? entityId, string field)
                {
                    if (entityId is null)
                    {
                        return;
                    }

                    if (entityIds is not null)
                    {
                        Require(entityIds.Contains(entityId),
                            $"Schedule '{scheduleId}' references unknown entity '{entityId}'.", $"{schedulePath}.{field}");
                    }
                }
            }
        }

        return new CompiledRuleSet(rules.MoveToImmutable(), schedules.ToImmutable())
        {
            Facts = [.. facts.OrderBy(fact => fact.Id, StringComparer.Ordinal)],
            Templates = spawnTemplates.ToImmutableDictionary(StringComparer.Ordinal),
            Bundles = bundles.ToImmutableDictionary(StringComparer.Ordinal),
        };
    }

    private static CompiledQuery CompileQuery(string ruleId, RulesContentFile.Condition condition,
        string conditionPath)
    {
        var source = condition.SelectorValue;
        Require(source is not null, $"Rule '{ruleId}' query requires selector.", $"{conditionPath}.selector");
        var selectorSource = source!.Value;
        var tag = selectorSource.Tag?.GetString();
        var property = selectorSource.Property?.GetString();
        var propertyComparisonName = selectorSource.PropertyComparison?.GetString();
        var propertyValue = selectorSource.PropertyValue is { } propertyThreshold
            ? (double)propertyThreshold
            : (double?)null;
        var radius = selectorSource.Radius is { } authoredRadius ? (double)authoredRadius : (double?)null;
        var orderName = selectorSource.Order?.GetString();
        var limit = selectorSource.Limit is { } authoredLimit ? (int)authoredLimit : 0;
        var origin = selectorSource.Origin is { } originValue
            ? ParseSubject(originValue.GetString(), null, null,
                $"Rule '{ruleId}' query selector", $"{conditionPath}.selector.origin")
            : RuleSubject.Source;

        Require(property is not null || propertyComparisonName is null,
            $"Rule '{ruleId}' selector propertyComparison requires property.", $"{conditionPath}.selector.property");
        Require(propertyValue is null || property is not null,
            $"Rule '{ruleId}' selector propertyValue requires property.", $"{conditionPath}.selector.propertyValue");
        Require(radius is not { } authoredRadiusValue || double.IsFinite(authoredRadiusValue) && authoredRadiusValue > 0,
            $"Rule '{ruleId}' selector radius must be positive and finite.", $"{conditionPath}.selector.radius");
        Require(limit is >= 0 and <= 10_000,
            $"Rule '{ruleId}' selector limit must be between 0 and 10,000.", $"{conditionPath}.selector.limit");
        var propertyComparison = propertyComparisonName switch
        {
            null => QueryComparison.AtLeast,
            "atLeast" => QueryComparison.AtLeast,
            "atMost" => QueryComparison.AtMost,
            "equals" => QueryComparison.Equals,
            _ => throw new ContentPackException(
                $"Rule '{ruleId}' selector has unknown property comparison '{propertyComparisonName}'.",
                $"{conditionPath}.selector.propertyComparison"),
        };
        var order = orderName switch
        {
            null or "id" => QueryOrder.EntityId,
            "nearest" => QueryOrder.Nearest,
            _ => throw new ContentPackException(
                $"Rule '{ruleId}' selector has unknown order '{orderName}'.",
                $"{conditionPath}.selector.order"),
        };
        var selector = new EntitySelector(tag, property, propertyComparison,
            propertyValue.GetValueOrDefault(), radius, origin, order, limit);

        var aggregateName = condition.Aggregate?.GetString();
        Require(aggregateName is not null,
            $"Rule '{ruleId}' query requires aggregate.", $"{conditionPath}.aggregate");
        var aggregate = aggregateName switch
        {
            "count" => QueryAggregateKind.Count,
            "sum" => QueryAggregateKind.Sum,
            "min" => QueryAggregateKind.Minimum,
            "max" => QueryAggregateKind.Maximum,
            "average" => QueryAggregateKind.Average,
            "nearest" => QueryAggregateKind.NearestDistance,
            _ => throw new ContentPackException(
                $"Rule '{ruleId}' has unknown query aggregate '{aggregateName}'.", $"{conditionPath}.aggregate"),
        };
        var aggregateProperty = condition.AggregateProperty?.GetString();
        Require(aggregate is QueryAggregateKind.Count or QueryAggregateKind.NearestDistance
            || !string.IsNullOrWhiteSpace(aggregateProperty),
            $"Rule '{ruleId}' {aggregateName} aggregate requires aggregateProperty.",
            $"{conditionPath}.aggregateProperty");

        var formulaSource = condition.Formula?.GetString();
        CompiledExpression? formula = null;
        if (formulaSource is not null)
        {
            try
            {
                formula = ExpressionCompiler.Compile(formulaSource);
            }
            catch (ExpressionCompileException exception)
            {
                throw new ContentPackException(
                    $"Rule '{ruleId}' query formula is invalid: {exception.Message}",
                    $"{conditionPath}.formula");
            }

            Require(formula.ResultKind == ExpressionResultKind.Boolean,
                $"Rule '{ruleId}' query formula must produce a boolean.", $"{conditionPath}.formula");
        }

        var comparisonName = condition.Comparison?.GetString();
        var threshold = condition.Value is { } authoredThreshold ? (double)authoredThreshold : (double?)null;
        Require(formula is not null || comparisonName is not null && threshold is { } value && double.IsFinite(value),
            $"Rule '{ruleId}' query requires formula or comparison and value.", $"{conditionPath}.comparison");
        var comparison = comparisonName switch
        {
            null => QueryComparison.AtLeast,
            "atLeast" => QueryComparison.AtLeast,
            "atMost" => QueryComparison.AtMost,
            "equals" => QueryComparison.Equals,
            _ => throw new ContentPackException(
                $"Rule '{ruleId}' has unknown query comparison '{comparisonName}'.",
                $"{conditionPath}.comparison"),
        };
        return new CompiledQuery(selector, aggregate, aggregateProperty, comparison,
            threshold.GetValueOrDefault(), formula);
    }

    private static SimulationFactPayload? CompilePayload(
        RulesContentFile.Effect.PayloadArray payload,
        string fieldPath,
        FactDefinition? fact)
    {
        var entries = new List<SimulationFactPayloadEntry>(payload.GetArrayLength());
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < payload.GetArrayLength(); index++)
        {
            entries.Add(CompilePayloadEntry(payload[index], $"{fieldPath}[{index}]", fact, keys));
        }

        // An explicit empty array is a payload with no fields, not an absent payload: emitting it must
        // not inherit the incoming fact's values.
        return new SimulationFactPayload(entries);
    }

    private static SimulationFactPayload? CompilePayload(
        RulesContentFile.Schedule.PayloadArray payload,
        string fieldPath,
        FactDefinition? fact)
    {
        var entries = new List<SimulationFactPayloadEntry>(payload.GetArrayLength());
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < payload.GetArrayLength(); index++)
        {
            entries.Add(CompilePayloadEntry(payload[index], $"{fieldPath}[{index}]", fact, keys));
        }

        // An explicit empty array is a payload with no fields, not an absent payload: emitting it must
        // not inherit the incoming fact's values.
        return new SimulationFactPayload(entries);
    }

    private static SimulationFactPayloadEntry CompilePayloadEntry(
        RulesContentFile.PayloadValue item,
        string itemPath,
        FactDefinition? fact,
        HashSet<string> keys)
    {
        var key = item.Key.GetString();
        Require(!string.IsNullOrWhiteSpace(key)
            && key.Length <= SimulationFactPayload.MaximumKeyLength,
            $"A fact payload key must be 1-{SimulationFactPayload.MaximumKeyLength} characters.",
            $"{itemPath}.key");
        Require(keys.Add(key!), $"Duplicate fact payload key '{key}'.", $"{itemPath}.key");
        FactFieldDefinition? field = null;
        if (fact is not null)
        {
            foreach (var candidate in fact.Fields)
            {
                if (string.Equals(candidate.Key, key, StringComparison.Ordinal))
                {
                    field = candidate;
                    break;
                }
            }

            Require(field is not null,
                $"Fact '{fact.Id}' does not declare payload field '{key}'.", $"{itemPath}.key");
        }

        return new SimulationFactPayloadEntry(key!,
            ParsePayloadValue(item.Value, field?.Kind ?? SimulationFactValueKind.None, itemPath));
    }

    private static SimulationFactValue ParsePayloadValue(
        RulesContentFile.PayloadValue.ValueEntity value,
        SimulationFactValueKind expectedKind,
        string fieldPath)
    {
        if (value.ValueKind == Corvus.Text.Json.JsonValueKind.Number)
        {
            if (expectedKind == SimulationFactValueKind.Integer)
            {
                Require(value.TryGetValue(out long integer),
                    "A declared integer payload value must be a whole number.", $"{fieldPath}.value");
                return SimulationFactValue.FromInteger(integer);
            }

            if (expectedKind is SimulationFactValueKind.Number or SimulationFactValueKind.None)
            {
                if (expectedKind == SimulationFactValueKind.None && value.TryGetValue(out long exact))
                {
                    return SimulationFactValue.FromInteger(exact);
                }

                Require(value.TryGetValue(out double number) && double.IsFinite(number),
                    "A numeric payload value must be finite.", $"{fieldPath}.value");
                return SimulationFactValue.FromNumber(number);
            }

            throw new ContentPackException(
                $"Payload value must be {expectedKind.ToString().ToLowerInvariant()}, not a number.",
                $"{fieldPath}.value");
        }

        if (value.ValueKind == Corvus.Text.Json.JsonValueKind.String)
        {
            Require(expectedKind is SimulationFactValueKind.Text or SimulationFactValueKind.None,
                $"Payload value must be {expectedKind.ToString().ToLowerInvariant()}, not text.",
                $"{fieldPath}.value");
            var text = value.GetString()!;
            Require(text.Length <= SimulationFactValue.MaximumTextLength,
                $"A text payload value exceeds the {SimulationFactValue.MaximumTextLength}-character limit.",
                $"{fieldPath}.value");
            return SimulationFactValue.FromText(text);
        }

        if (value.ValueKind is Corvus.Text.Json.JsonValueKind.True or Corvus.Text.Json.JsonValueKind.False)
        {
            Require(expectedKind is SimulationFactValueKind.Boolean or SimulationFactValueKind.None,
                $"Payload value must be {expectedKind.ToString().ToLowerInvariant()}, not boolean.",
                $"{fieldPath}.value");
            Require(value.TryGetValue(out bool boolean),
                "A boolean payload value is malformed.", $"{fieldPath}.value");
            return SimulationFactValue.FromBoolean(boolean);
        }

        throw new ContentPackException(
            "A fact payload value must be a number, text, or boolean.", $"{fieldPath}.value");
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
    private static RuleSubject ParseSubject(string? subject, RulesContentFile.RowSubject? row, string? factRow,
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

        if (row is { } rowSubject)
        {
            var population = rowSubject.Population.GetString();
            var slot = rowSubject.Slot is { } slotValue ? (int)slotValue : 0;
            if (string.IsNullOrWhiteSpace(population))
            {
                throw Error("row subject requires a population.");
            }

            if (slot < 0)
            {
                throw Error("row subject slot must be non-negative.");
            }

            return RuleSubject.Row(population, slot);
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

    private static byte[] ReadGameBytes(
        string path,
        string displayPath,
        string schemaKey,
        string schemaText,
        ContentFingerprintBuilder fingerprint) =>
        ContentFileReader.ReadValidatedBytes(path, displayPath, schemaKey, schemaText, fingerprint);

    private static byte[] ReadRulesBytes(
        string path,
        string displayPath,
        string schemaKey,
        string schemaText,
        ContentFingerprintBuilder fingerprint) =>
        ContentFileReader.ReadValidatedBytes(path, displayPath, schemaKey, schemaText, fingerprint);

    // Modules receive their declaration as a detached JsonElement, so it outlives the parsed game document.
    private static Dictionary<string, JsonElement>? ReadModuleDeclarations(byte[] gameBytes)
    {
        using var document = JsonDocument.Parse(gameBytes);
        if (!document.RootElement.TryGetProperty("modules"u8, out var modules))
        {
            return null;
        }

        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var declaration in modules.EnumerateObject())
        {
            result[declaration.Name] = declaration.Value.Clone();
        }

        return result;
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
