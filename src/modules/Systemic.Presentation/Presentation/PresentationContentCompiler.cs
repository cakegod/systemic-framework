using System.Collections.Immutable;
using System.Text.Json;
using Systemic.Content;

namespace Systemic.Presentation;

internal static class PresentationContentCompiler
{
    private const string VisualsSchemaId = "https://press-the-button.dev/systemic/visuals.schema.json";
    private const string UiSchemaId = "https://press-the-button.dev/systemic/ui.schema.json";
    private const string PresentationSchemaId = "https://press-the-button.dev/systemic/presentation.schema.json";
    private static readonly string s_visualsSchema = ReadSchema("visuals.schema.json");
    private static readonly string s_uiSchema = ReadSchema("ui.schema.json");
    private static readonly string s_presentationSchema = ReadSchema("presentation.schema.json");

    public static PresentationModuleContent Compile(ContentModuleContext context, JsonElement declaration)
    {
        var visualsFile = context.ReadFile<VisualsFile>(declaration, "visuals", "$",
            VisualsSchemaId, s_visualsSchema, out var visualsPath);
        var uiFile = context.ReadFile<UiFile>(declaration, "ui", "$",
            UiSchemaId, s_uiSchema, out var uiPath);
        var presentationFile = context.ReadFile<PresentationFile>(declaration, "presentation", "$",
            PresentationSchemaId, s_presentationSchema, out var presentationPath);
        var visuals = CompileVisuals(visualsFile);
        foreach (var recipe in visuals.Recipes)
        {
            context.References.Add("visualRecipe", recipe.Id, visualsPath);
        }

        foreach (var entity in context.Entities.Definitions)
        {
            if (entity.VisualRecipeId is { } visualId)
            {
                var source = entity.FieldSources.GetValueOrDefault("visualRecipeId")
                    ?? new ContentFieldSource(context.EntitiesSourceFile ?? "entities.json", "$.entities");
                context.References.Require("visualRecipe", visualId, source.File, source.Path);
            }
        }

        var choiceIcons = (presentationFile.ChoiceIcons ?? []).ToImmutableDictionary(StringComparer.Ordinal);
        var catalog = CompilePresentation(presentationFile, context, presentationPath, visuals);
        var ui = CompileUi(uiFile, context, uiPath, choiceIcons,
            visuals.Recipes.Select(recipe => recipe.Id).ToHashSet(StringComparer.Ordinal));
        return new PresentationModuleContent(visuals, ui, catalog, choiceIcons);
    }

    private static string ReadSchema(string fileName)
    {
        using var stream = typeof(PresentationContentCompiler).Assembly.GetManifestResourceStream(
            $"Systemic.Presentation.Schemas.{fileName}")
            ?? throw new InvalidOperationException($"Embedded presentation schema '{fileName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static CompiledVisuals CompileVisuals(VisualsFile file)
    {
        var palettes = new Dictionary<string, ImmutableDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (paletteId, sourceColors) in file.Palettes ?? [])
        {
            var colors = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
            foreach (var (role, color) in sourceColors.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                colors.Add(role, color!);
            }

            palettes.Add(paletteId, colors.ToImmutable());
        }

        var fragments = new Dictionary<string, VisualFragmentFile>(StringComparer.Ordinal);
        var sourceFragments = file.Fragments ?? [];
        foreach (var sourceFragment in sourceFragments)
        {
            var fragment = sourceFragment!;
            Require(fragments.TryAdd(fragment.Id!, fragment), $"Duplicate visual fragment id '{fragment.Id}'.");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sourceRecipes = file.Recipes!;
        var recipes = new Dictionary<string, VisualRecipe>(StringComparer.Ordinal);
        var recipeParts = new Dictionary<string, VisualPartFile[]>(StringComparer.Ordinal);
        foreach (var recipe in sourceRecipes)
        {
            var visualRecipe = recipe!;
            Require(ids.Add(visualRecipe.Id!), $"Duplicate visual recipe id '{visualRecipe.Id}'.");
            var importedMode = visualRecipe.ImportedMode switch
            {
                null or "underlay" => ImportedAssetMode.Underlay,
                "overlay" => ImportedAssetMode.Overlay,
                "replace" => ImportedAssetMode.Replace,
                _ => throw new ContentPackException(
                    $"Visual recipe '{visualRecipe.Id}' has unknown importedMode '{visualRecipe.ImportedMode}'.", "$.recipes.importedMode"),
            };
            ImmutableArray<string> palette;
            ImmutableDictionary<string, int> paletteRoles;
            if (visualRecipe.PaletteId is { } paletteId)
            {
                Require(palettes.TryGetValue(paletteId, out var namedPalette), $"Visual recipe '{visualRecipe.Id}' references unknown palette '{paletteId}'.");
                var orderedColors = namedPalette!.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                palette = [.. orderedColors.Select(pair => pair.Value)];
                paletteRoles = orderedColors.Select((pair, index) => (Role: pair.Key, Index: index))
                    .ToImmutableDictionary(pair => pair.Role, pair => pair.Index, StringComparer.Ordinal);
            }
            else
            {
                palette = [.. visualRecipe.Palette!];
                paletteRoles = ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal);
            }

            var sourceLayers = new List<VisualLayerFile>();
            foreach (var fragmentId in visualRecipe.UseFragments ?? [])
            {
                Require(fragments.TryGetValue(fragmentId, out var fragment), $"Visual recipe '{visualRecipe.Id}' references unknown fragment '{fragmentId}'.");
                sourceLayers.AddRange(fragment!.Layers!);
            }

            sourceLayers.AddRange(visualRecipe.Layers ?? []);
            Require(sourceLayers.Count <= 512, $"Visual recipe '{visualRecipe.Id}' exceeds the 512-layer limit.");
            var layers = ImmutableArray.CreateBuilder<VisualLayer>(sourceLayers.Count);
            foreach (var sourceLayer in sourceLayers)
            {
                var layer = sourceLayer!;
                var colorIndex = layer.ColorRole is { } colorRole
                    ? paletteRoles.TryGetValue(colorRole, out var resolvedColor)
                        ? resolvedColor
                        : throw new ContentPackException($"Visual recipe '{visualRecipe.Id}' references unknown palette color role '{colorRole}'.")
                    : layer.Color!.Value;
                Require(colorIndex >= 0 && colorIndex < palette.Length, $"Visual recipe '{visualRecipe.Id}' has an invalid layer color index.");
                Require(double.IsFinite(layer.X) && double.IsFinite(layer.Y)
                    && double.IsFinite(layer.Width) && double.IsFinite(layer.Height), $"Visual recipe '{visualRecipe.Id}' has non-finite layer geometry.");
                layers.Add(new VisualLayer(layer.Shape, colorIndex, layer.X, layer.Y, layer.Width, layer.Height,
                    layer.JitterPixels, layer.State));
            }

            recipes.Add(visualRecipe.Id!, new VisualRecipe(visualRecipe.Id!, visualRecipe.Kind, visualRecipe.Seed,
                visualRecipe.CanvasWidth, visualRecipe.CanvasHeight,
                palette, layers.MoveToImmutable(), paletteRoles,
                ImportedAssetId: visualRecipe.ImportedAssetId, ImportedMode: importedMode));
            recipeParts.Add(visualRecipe.Id!, visualRecipe.Parts ?? []);
        }

        foreach (var (recipeId, parts) in recipeParts)
        {
            if (parts.Length == 0)
            {
                continue;
            }

            var compiledParts = ImmutableArray.CreateBuilder<VisualPart>(parts.Length);
            foreach (var part in parts)
            {
                Require(recipes.TryGetValue(part.RecipeId!, out var child),
                    $"Visual recipe '{recipeId}' references unknown part recipe '{part.RecipeId}'.");
                Require(recipeParts[part.RecipeId!].Length == 0,
                    $"Visual recipe '{recipeId}' cannot compose nested part recipe '{part.RecipeId}'.");
                compiledParts.Add(new VisualPart(child!, part.X, part.Y, part.Scale));
            }

            recipes[recipeId] = recipes[recipeId] with { Parts = compiledParts.MoveToImmutable() };
        }

        for (var recipeIndex = 0; recipeIndex < sourceRecipes.Length; recipeIndex++)
        {
            var recipeId = sourceRecipes[recipeIndex]!.Id!;
            var recipe = recipes[recipeId];
            if (ProceduralRasterizer.EstimatePixelWork(recipe) > ProceduralRasterizer.MaximumPixelWork)
            {
                var field = recipe.Parts.IsDefaultOrEmpty ? "layers" : "parts";
                throw new ContentPackException(
                    $"Visual recipe '{recipeId}' exceeds the raster work budget of {ProceduralRasterizer.MaximumPixelWork} pixel operations.",
                    $"$.recipes[{recipeIndex}].{field}");
            }
        }

        return new CompiledVisuals([.. recipes.Values]);
    }

    private static CompiledUi CompileUi(
        UiFile file,
        ContentModuleContext context,
        string sourcePath,
        ImmutableDictionary<string, string> choiceIcons,
        HashSet<string> visualIds)
    {
        var entitiesById = context.Entities.Definitions.ToDictionary(definition => definition.Id, StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var sourceLayouts = file.Layouts!;
        var layouts = ImmutableArray.CreateBuilder<UiLayout>(sourceLayouts.Length);
        var nodeBudget = new UiNodeBudget();
        for (var layoutIndex = 0; layoutIndex < sourceLayouts.Length; layoutIndex++)
        {
            var layoutPath = $"$.layouts[{layoutIndex}]";
            var layout = sourceLayouts[layoutIndex]!;
            Require(ids.Add(layout.Id!), $"Duplicate UI layout id '{layout.Id}'.", $"{layoutPath}.id");
            layouts.Add(new UiLayout(layout.Id!, CompileNode(layout.Root, layout.Id!, $"{layoutPath}.root", 0, nodeBudget,
                context, sourcePath, entitiesById, choiceIcons, visualIds)));
        }

        return new CompiledUi(layouts.MoveToImmutable());
    }

    private static PresentationCatalog CompilePresentation(PresentationFile file, ContentModuleContext context, string sourcePath, CompiledVisuals visuals)
    {
        var recipeIds = visuals.Recipes.Select(recipe => recipe.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (choiceId, recipeId) in file.ChoiceIcons ?? [])
        {
            Require(recipeIds.Contains(recipeId),
                $"Choice '{choiceId}' references unknown visual recipe '{recipeId}'.", $"$.choiceIcons.{choiceId}");
            context.References.Require("choice", choiceId, sourcePath, $"$.choiceIcons.{choiceId}");
        }
        var timelines = file.Timelines!;
        var sourceBindings = file.Bindings!;

        var visualIds = visuals.Recipes.Select(recipe => recipe.Id).ToHashSet(StringComparer.Ordinal);
        var timelineIds = new HashSet<string>(StringComparer.Ordinal);
        var compiledTimelines = ImmutableArray.CreateBuilder<PresentationTimeline>(timelines.Length);
        for (var index = 0; index < timelines.Length; index++)
        {
            var path = $"$.timelines[{index}]";
            var source = timelines[index]!;
            Require(timelineIds.Add(source.Id!), $"Duplicate timeline id '{source.Id}'.", $"{path}.id");
            Require(visualIds.Contains(source.VisualRecipeId!),
                $"Timeline '{source.Id}' references unknown visual recipe '{source.VisualRecipeId}'.", $"{path}.visualRecipeId");

            var sourceKeyframes = source.Keyframes!;
            var keyframes = ImmutableArray.CreateBuilder<PresentationKeyframe>(sourceKeyframes.Length);
            var previousTick = -1;
            for (var frameIndex = 0; frameIndex < sourceKeyframes.Length; frameIndex++)
            {
                var framePath = $"{path}.keyframes[{frameIndex}]";
                var frame = sourceKeyframes[frameIndex]!;
                Require(frame.Tick < source.DurationTicks && frame.Tick > previousTick,
                    $"Timeline '{source.Id}' keyframe ticks must be ordered, unique, and inside the duration.", $"{framePath}.tick");
                Require(float.IsFinite(frame.X) && float.IsFinite(frame.Y),
                    $"Timeline '{source.Id}' keyframe offsets must be finite.", framePath);
                Require(float.IsFinite(frame.Scale),
                    $"Timeline '{source.Id}' keyframe scale must be finite.", $"{framePath}.scale");
                Require(float.IsFinite(frame.Opacity),
                    $"Timeline '{source.Id}' keyframe opacity must be finite.", $"{framePath}.opacity");
                if (frameIndex == 0)
                {
                    Require(frame.Tick == 0, $"Timeline '{source.Id}' must start with a keyframe at tick zero.", $"{framePath}.tick");
                }

                keyframes.Add(new PresentationKeyframe(frame.Tick, frame.X, frame.Y,
                    frame.Scale, frame.Opacity, frame.VisualState));
                previousTick = frame.Tick;
            }

            compiledTimelines.Add(new PresentationTimeline(source.Id!, source.VisualRecipeId!,
                source.DurationTicks, source.Loop, source.ParticleCount, keyframes.MoveToImmutable()));
        }

        var bindingKeys = new HashSet<(string Event, string Timeline, string Anchor)>();
        var bindings = ImmutableArray.CreateBuilder<PresentationCueBinding>(sourceBindings.Length);
        for (var index = 0; index < sourceBindings.Length; index++)
        {
            var path = $"$.bindings[{index}]";
            var source = sourceBindings[index]!;
            Require(timelineIds.Contains(source.TimelineId!),
                $"Presentation binding references unknown timeline '{source.TimelineId}'.", $"{path}.timelineId");
            var anchor = source.Anchor switch
            {
                "source" => PresentationAnchor.Source,
                "target" => PresentationAnchor.Target,
                "midpoint" => PresentationAnchor.Midpoint,
                _ => throw new ContentPackException($"Presentation binding has unknown anchor '{source.Anchor}'.", $"{path}.anchor"),
            };
            Require(bindingKeys.Add((source.Event!, source.TimelineId!, source.Anchor!)),
                $"Duplicate presentation binding for event '{source.Event}' and timeline '{source.TimelineId}'.", path);
            bindings.Add(new PresentationCueBinding(source.Event!, source.TimelineId!, anchor, source.SeedSalt));
        }

        return new PresentationCatalog(compiledTimelines.MoveToImmutable(), bindings.MoveToImmutable());
    }

    private static UiNode CompileNode(
        UiNodeFile node,
        string layoutId,
        string fieldPath,
        int depth,
        UiNodeBudget budget,
        ContentModuleContext context,
        string sourcePath,
        IReadOnlyDictionary<string, EntityDefinition> entitiesById,
        ImmutableDictionary<string, string> choiceIcons,
        HashSet<string> visualIds,
        bool insideListItem = false)
    {
        Require(depth <= 32, $"UI layout '{layoutId}' exceeds the maximum nesting depth of 32.", fieldPath);
        Require(++budget.Count <= 16_384, "ui.json exceeds the 16,384-node limit.", fieldPath);
        Require(node.Type is "column" or "row" or "text" or "value" or "bar" or "button" or "icon" or "list" or "toggle",
            $"UI layout '{layoutId}' has unknown node type '{node.Type}'.", $"{fieldPath}.type");
        if (node.Type == "button")
        {
            Require(node.Action is not null, $"UI button in '{layoutId}' requires an action ID.", $"{fieldPath}.action");
            Require(node.Amount is null or (>= 1 and <= 1_000_000),
                $"UI button in '{layoutId}' requires a buy amount between 1 and 1,000,000.", $"{fieldPath}.amount");
        }
        else
        {
            Require(node.Amount is null,
                $"UI node '{node.Type}' in '{layoutId}' cannot define a buy amount.", $"{fieldPath}.amount");
        }

        if (node.Type == "toggle")
        {
            Require(node.Bind is not null,
                $"UI toggle in '{layoutId}' requires a binding that reads as a boolean.", $"{fieldPath}.bind");
        }

        UiBindingDescriptor? bindingDescriptor = null;
        if (node.Bind is { Length: > 0 } binding)
        {
            bindingDescriptor = UiBindingParser.Parse(binding);
            Require(bindingDescriptor.HasGrammarShape,
                $"UI binding '{node.Bind}' must use world., event., or selection. followed by a value path.", $"{fieldPath}.bind");
            ValidateUiBinding(bindingDescriptor, layoutId, $"{fieldPath}.bind", sourcePath, context, entitiesById,
                insideListItem);
        }

        if (node.Type == "bar")
        {
            Require(node.Bind is not null, $"UI bar in '{layoutId}' requires a numeric binding.", $"{fieldPath}.bind");
            var cooldownBinding = bindingDescriptor is { Kind: UiBindingKind.Choice, ValuePath: "cooldown" };
            Require(cooldownBinding || node.Minimum is not null && node.Maximum is not null,
                $"UI bar in '{layoutId}' requires minimum and maximum unless it binds to choice cooldown progress.", fieldPath);
            if (node.Minimum is { } minimum && node.Maximum is { } maximum)
            {
                Require(double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum,
                    $"UI bar in '{layoutId}' requires a finite range where maximum is greater than minimum.", fieldPath);
            }
        }
        else
        {
            Require(node.Minimum is null && node.Maximum is null,
                $"UI node '{node.Type}' in '{layoutId}' cannot define a numeric range.", fieldPath);
        }

        if (node.Type == "list")
        {
            Require(bindingDescriptor is { Kind: UiBindingKind.List, ValuePath: { Length: > 0 } },
                $"UI list in '{layoutId}' requires a list.tag: binding.", $"{fieldPath}.bind");
            Require(node.Children is { Length: 1 },
                $"UI list in '{layoutId}' requires exactly one child template.", $"{fieldPath}.children");
        }

        var visualRecipeId = node.VisualRecipeId;
        if (node.Type == "icon")
        {
            if (bindingDescriptor is { Kind: UiBindingKind.Choice, EntityPath: { } choiceId })
            {
                Require(choiceIcons.TryGetValue(choiceId, out var choiceRecipeId),
                    $"UI icon in '{layoutId}' has no Presentation recipe assignment for choice '{choiceId}'.", $"{fieldPath}.bind");
                Require(visualRecipeId is null || visualRecipeId == choiceRecipeId,
                    $"UI icon in '{layoutId}' must use the Presentation choice recipe assigned to '{choiceId}'.", $"{fieldPath}.visualRecipeId");
                visualRecipeId = choiceRecipeId;
            }

            Require(visualRecipeId is not null && visualIds.Contains(visualRecipeId),
                $"UI icon in '{layoutId}' references unknown visual recipe '{visualRecipeId}'.", $"{fieldPath}.visualRecipeId");
        }
        else
        {
            Require(visualRecipeId is null,
                $"Only UI icon nodes in '{layoutId}' can define visualRecipeId.", $"{fieldPath}.visualRecipeId");
        }

        var children = node.Children ?? [];
        var compiledChildren = ImmutableArray.CreateBuilder<UiNode>(children.Length);
        for (var childIndex = 0; childIndex < children.Length; childIndex++)
        {
            compiledChildren.Add(CompileNode(children[childIndex]!, layoutId, $"{fieldPath}.children[{childIndex}]", depth + 1, budget,
                context, sourcePath, entitiesById, choiceIcons, visualIds,
                insideListItem || node.Type == "list"));
        }

        return new UiNode(node.Type, node.Text, node.Bind, node.Action, compiledChildren.MoveToImmutable(),
            node.Minimum, node.Maximum, visualRecipeId, node.Tooltip, node.Amount);
    }

    private static void ValidateUiBinding(
        UiBindingDescriptor binding,
        string layoutId,
        string fieldPath,
        string sourcePath,
        ContentModuleContext context,
        IReadOnlyDictionary<string, EntityDefinition> entitiesById,
        bool insideListItem)
    {
        if (binding.Kind is UiBindingKind.Selection or UiBindingKind.List)
        {
            // A list tag is checked against the live world at evaluation time, and the item template is
            // shared by every tag that renders through it.
            return;
        }

        if (binding.Kind == UiBindingKind.Item)
        {
            // Outside a list template there is no item, so the binding would silently render nothing.
            Require(insideListItem,
                $"UI layout '{layoutId}' binds '{binding.Binding}' outside a list template.", fieldPath);
            return;
        }

        if (binding.Kind == UiBindingKind.Event)
        {
            Require(binding.ValuePath is "type" or "sourceId" or "targetId" or "depth",
                $"UI layout '{layoutId}' binds to unknown event field '{binding.Binding}'.", fieldPath);
            return;
        }

        if (binding.Kind == UiBindingKind.Choice)
        {
            var choiceId = binding.EntityPath;
            Require(choiceId is not null,
                $"UI layout '{layoutId}' has invalid build choice binding '{binding.Binding}'.", fieldPath);
            var field = binding.ValuePath!;
            Require(field.Length > 0, $"UI layout '{layoutId}' has an empty module binding field.", fieldPath);
            context.References.Require("choice", choiceId!, sourcePath, fieldPath);
            return;
        }

        if (binding.Kind == UiBindingKind.WorldTick)
        {
            return;
        }

        if (binding.Kind == UiBindingKind.World)
        {
            foreach (var candidate in binding.WorldCandidates)
            {
                if (!entitiesById.TryGetValue(candidate.EntityPath, out var definition))
                {
                    continue;
                }

                if (candidate.Tag is { } tag)
                {
                    Require(tag.Length > 0,
                        $"UI layout '{layoutId}' has an empty tag binding for entity '{candidate.EntityPath}'.", fieldPath);
                    return;
                }

                Require(definition.Properties.ContainsKey(candidate.MemberPath),
                    $"UI layout '{layoutId}' references unknown property '{candidate.MemberPath}' on entity '{candidate.EntityPath}'.", fieldPath);
                return;
            }

            throw new ContentPackException($"UI layout '{layoutId}' references unknown world entity or property in '{binding.Binding}'.", fieldPath);
        }

        Require(binding.Kind == UiBindingKind.World,
            $"UI layout '{layoutId}' has unsupported binding '{binding.Binding}'.", fieldPath);
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

    private sealed class UiNodeBudget { public int Count { get; set; } }

    private sealed record VisualsFile(
        int SchemaVersion,
        VisualRecipeFile[]? Recipes = null,
        Dictionary<string, Dictionary<string, string>>? Palettes = null,
        VisualFragmentFile[]? Fragments = null);

    private sealed record PresentationFile(
        int SchemaVersion,
        PresentationTimelineFile[]? Timelines = null,
        PresentationBindingFile[]? Bindings = null,
        Dictionary<string, string>? ChoiceIcons = null);

    private sealed record PresentationTimelineFile(
        string Id,
        string? VisualRecipeId,
        int DurationTicks,
        bool Loop,
        int ParticleCount = 0,
        PresentationKeyframeFile[]? Keyframes = null);

    private sealed record PresentationKeyframeFile(
        int Tick,
        float X = 0,
        float Y = 0,
        float Scale = 1,
        float Opacity = 1,
        string? VisualState = null);

    private sealed record PresentationBindingFile(
        string Event,
        string? TimelineId,
        string? Anchor,
        long SeedSalt = 0);

    private sealed record VisualFragmentFile(string Id, VisualLayerFile[]? Layers = null);

    private sealed record VisualRecipeFile(
        string Id,
        string Kind,
        long Seed,
        int CanvasWidth,
        int CanvasHeight,
        string[]? Palette = null,
        VisualLayerFile[]? Layers = null,
        string? PaletteId = null,
        string[]? UseFragments = null,
        VisualPartFile[]? Parts = null,
        string? ImportedAssetId = null,
        string? ImportedMode = null);

    private sealed record VisualPartFile(string RecipeId, int X, int Y, int Scale = 1);

    private sealed record VisualLayerFile(
        string Shape,
        float X,
        float Y,
        float Width,
        float Height,
        int? Color = null,
        int JitterPixels = 0,
        string? ColorRole = null,
        string? State = null);
    private sealed record UiFile(int SchemaVersion, UiLayoutFile[]? Layouts = null);
    private sealed record UiLayoutFile(string Id, UiNodeFile Root);
    private sealed record UiNodeFile(
        string Type,
        string? Text = null,
        string? Bind = null,
        string? Action = null,
        UiNodeFile[]? Children = null,
        double? Minimum = null,
        double? Maximum = null,
        string? VisualRecipeId = null,
        string? Tooltip = null,
        int? Amount = null);
}
