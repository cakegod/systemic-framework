using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;
using Systemic.Presentation;
using Systemic.Rules;
using Systemic.Save;
using Systemic.Ui;

namespace Systemic.Tests.Presentation;

public sealed class PresentationPipelineTests
{
    [Fact]
    public void Imported_pixels_compose_with_generated_layers_and_missing_assets_fall_back()
    {
        var importedPixels = new PixelBuffer(2, 1, [0, 0, 255, 255, 0, 0, 255, 255]);
        var provider = new TestAssetProvider(new Dictionary<string, PixelBuffer>
        {
            ["asset.hero"] = importedPixels,
        });
        var underlay = Recipe(ImportedAssetMode.Underlay);
        var overlay = Recipe(ImportedAssetMode.Overlay);
        var replace = Recipe(ImportedAssetMode.Replace);

        var underlayResult = VisualAssetResolver.Resolve(underlay, provider);
        var overlayResult = VisualAssetResolver.Resolve(overlay, provider);
        var replaceResult = VisualAssetResolver.Resolve(replace, provider);
        var missingResult = VisualAssetResolver.Resolve(underlay, provider: null);

        Assert.Equal(new Rgba32(255, 0, 0, 255), underlayResult.Pixels.GetPixel(0, 0));
        Assert.Equal(new Rgba32(0, 0, 255, 255), underlayResult.Pixels.GetPixel(1, 0));
        Assert.Equal(new Rgba32(0, 0, 255, 255), overlayResult.Pixels.GetPixel(0, 0));
        Assert.Equal(new Rgba32(0, 0, 255, 255), replaceResult.Pixels.GetPixel(0, 0));
        Assert.True(replaceResult.UsedImportedAsset);
        Assert.Equal(["asset.hero"], missingResult.MissingAssetIds);
        Assert.False(missingResult.UsedImportedAsset);
        Assert.Equal(new Rgba32(255, 0, 0, 255), missingResult.Pixels.GetPixel(0, 0));
    }

    [Fact]
    public void Timeline_keyframes_interpolate_motion_and_stop_at_duration()
    {
        var timeline = new PresentationTimeline("spark", "sprite", 10, false, 3,
            [new PresentationKeyframe(0, Scale: 1, Opacity: 1, VisualState: "start"),
                new PresentationKeyframe(9, OffsetX: 9, OffsetY: -9, Scale: 3, Opacity: 0, VisualState: "end")]);
        var cue = new PresentationCue(1, 20, "spark", "spark", 1, 2,
            new Point2(1, 1), new Point2(10, 20), new Point2(10, 20), 77, 3, 0, 1);

        Assert.True(PresentationTimelineEvaluator.TrySample(timeline, cue, 4, out var frame));
        Assert.Equal(new Point2(14, 16), frame.Position);
        Assert.Equal(1 + (2 * 4.0 / 9), frame.Scale, precision: 6);
        Assert.Equal(1 - (4.0 / 9), frame.Opacity, precision: 6);
        Assert.Equal("start", frame.VisualState);
        Assert.False(PresentationTimelineEvaluator.TrySample(timeline, cue, 10, out _));
    }

    [Fact]
    public void Cue_stream_orders_entries_and_drops_the_oldest_with_a_counter()
    {
        var stream = new PresentationCueStream(capacity: 2);

        var firstSequence = stream.Enqueue(Cue("first"));
        var secondSequence = stream.Enqueue(Cue("second"));
        var thirdSequence = stream.Enqueue(Cue("third"));

        Assert.Equal([1, 2, 3], new[] { firstSequence, secondSequence, thirdSequence });
        Assert.Equal(1, stream.DroppedCount);
        Assert.Equal(2, stream.Count);
        Assert.True(stream.TryDequeue(out var second));
        Assert.True(stream.TryDequeue(out var third));
        Assert.Equal("second", second.FactType);
        Assert.Equal(2, second.Sequence);
        Assert.Equal("third", third.FactType);
        Assert.Equal(3, third.Sequence);
        Assert.False(stream.TryDequeue(out _));
    }

    [Fact]
    public void Cue_stream_rejects_invalid_metadata_without_consuming_a_sequence()
    {
        var stream = new PresentationCueStream(capacity: 2);

        Assert.Throws<ArgumentException>(() => stream.Enqueue(Cue("broken") with { Amount = double.NaN }));
        Assert.Throws<ArgumentException>(() => stream.Enqueue(Cue("broken") with { Tick = -1 }));
        Assert.Equal(1, stream.Enqueue(Cue("valid")));
        Assert.Equal(0, stream.DroppedCount);
    }

    [Fact]
    public void Cue_publication_is_ordered_post_commit_and_uses_position_snapshots()
    {
        var content = ContentWithCueRules();
        var host = HeadlessSystemicHost.Create(content, seed: 81);
        var sourceId = host.Game.EntityId("quarry.ember-source");
        var targetId = host.Game.EntityId("quarry.ore");
        host.Game.World.SetSpatialBounds(host.Game.World.ById(sourceId)!, new Bounds2(4, 5, 1, 1));
        host.Game.World.SetSpatialBounds(host.Game.World.ById(targetId)!, new Bounds2(20, 30, 2, 2));

        var result = host.Publish(new SimulationFact("cue.start", sourceId, targetId, Amount: 7));

        Assert.False(result.RolledBack);
        Assert.Equal(2, host.Game.PresentationCues().Count);
        Assert.True(host.Game.PresentationCues().TryDequeue(out var start));
        Assert.True(host.Game.PresentationCues().TryDequeue(out var chain));
        Assert.Equal("cue.start", start.FactType);
        Assert.Equal("cue.chain", chain.FactType);
        Assert.Equal(1, start.Sequence);
        Assert.Equal(2, chain.Sequence);
        Assert.Equal(new Point2(20, 30), start.TargetPosition);
        Assert.Equal(new Point2(20, 30), start.AnchorPosition);
        Assert.Equal(7, start.Amount);
        Assert.NotEqual(0UL, start.VisualSeed);
    }

    [Fact]
    public void Explicit_fact_position_anchors_presentation_and_emitted_reactions()
    {
        var host = HeadlessSystemicHost.Create(ContentWithCueRules(), seed: 81);
        var sourceId = host.Game.EntityId("quarry.ember-source");
        var targetId = host.Game.EntityId("quarry.ore");
        host.Game.World.SetSpatialBounds(host.Game.World.ById(targetId)!, new Bounds2(20, 30, 2, 2));

        host.Publish(new SimulationFact("cue.start", sourceId, targetId, Position: new(123, -45)));

        Assert.True(host.Game.PresentationCues().TryDequeue(out var start));
        Assert.True(host.Game.PresentationCues().TryDequeue(out var chain));
        Assert.Equal(new Point2(123, -45), start.AnchorPosition);
        Assert.Equal(new Point2(123, -45), chain.AnchorPosition);
    }

    [Fact]
    public void Cue_publication_waits_for_the_enclosing_world_transaction()
    {
        var host = HeadlessSystemicHost.Create(ContentWithCueRules(), seed: 811);
        var sourceId = host.Game.EntityId("quarry.ember-source");
        var targetId = host.Game.EntityId("quarry.ore");
        var initialOrdinal = host.Game.Presentation().Identity.NextCueSeedOrdinal;

        using (var transaction = host.Game.World.BeginTransaction())
        {
            host.Publish(new SimulationFact("cue.start", sourceId, targetId));

            Assert.Equal(0, host.Game.PresentationCues().Count);
            Assert.Equal(initialOrdinal, host.Game.Presentation().Identity.NextCueSeedOrdinal);
            transaction.Commit();
        }

        Assert.Equal(2, host.Game.PresentationCues().Count);
        Assert.Equal(initialOrdinal + 2, host.Game.Presentation().Identity.NextCueSeedOrdinal);
    }

    [Fact]
    public void Cue_publication_is_discarded_when_the_enclosing_world_transaction_rolls_back()
    {
        var host = HeadlessSystemicHost.Create(ContentWithCueRules(), seed: 812);
        var sourceId = host.Game.EntityId("quarry.ember-source");
        var targetId = host.Game.EntityId("quarry.ore");
        var initialOrdinal = host.Game.Presentation().Identity.NextCueSeedOrdinal;

        using (var transaction = host.Game.World.BeginTransaction())
        {
            host.Publish(new SimulationFact("cue.start", sourceId, targetId));

            Assert.Equal(0, host.Game.PresentationCues().Count);
            Assert.Equal(initialOrdinal, host.Game.Presentation().Identity.NextCueSeedOrdinal);
            transaction.Rollback();
        }

        Assert.Equal(0, host.Game.PresentationCues().Count);
        Assert.Equal(initialOrdinal, host.Game.Presentation().Identity.NextCueSeedOrdinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Invalid_fact_metadata_is_rejected_before_rule_effects_commit(bool negativeSourceId)
    {
        var host = HeadlessSystemicHost.Create(ContentWithCueRules(), seed: 82);
        var sourceId = host.Game.EntityId("quarry.ember-source");
        var targetId = host.Game.EntityId("quarry.ore");
        var target = host.Game.World.ById(targetId)!;
        var fact = negativeSourceId
            ? new SimulationFact("cue.start", -1, targetId)
            : new SimulationFact("cue.start", sourceId, targetId, Amount: double.NaN);

        Assert.Throws<ArgumentOutOfRangeException>(() => host.Publish(fact));

        Assert.Equal(0, host.Game.World.EffectiveProperty(target, "balance"));
        Assert.Equal(0, host.Game.PresentationCues().Count);
    }

    [Fact]
    public void Rolled_back_rules_suppress_cues_and_cue_seeds_survive_save_restore()
    {
        var sampleContent = SystemicTestContent.Load(SamplePath());
        var timeline = new PresentationTimeline("cue", "quarry.ember-spark", 8, false, 2,
            [new PresentationKeyframe(0)]);
        var presentation = new PresentationCatalog([timeline], [
            new PresentationCueBinding("cue.start", "cue", PresentationAnchor.Target, 13),
            new PresentationCueBinding("cue.broken", "cue", PresentationAnchor.Target, 13),
        ]);
        var rollbackContent = SystemicTestContent.WithPresentation(sampleContent,
            SystemicTestContent.Presentation(sampleContent) with { Catalog = presentation }) with
        {
            Rules = new CompiledRuleSet([
                new CompiledRule("cue.rollback", "cue.broken", 0, [],
                    [RuleEffect.AdjustProperty(RuleSubject.Target, "balance", double.MaxValue)])
            ]),
        };
        var rollbackHost = HeadlessSystemicHost.Create(rollbackContent, seed: 83);
        var oreId = rollbackHost.Game.EntityId("quarry.ore");
        rollbackHost.Game.World.ById(oreId)!.TrySetProperty(rollbackHost.Game.World, "balance", double.MaxValue);
        var rolledBack = rollbackHost.Publish(new SimulationFact("cue.broken", oreId, oreId));

        Assert.True(rolledBack.RolledBack);
        Assert.Equal(0, rollbackHost.Game.PresentationCues().Count);

        var stableContent = rollbackContent with
        {
            Rules = new CompiledRuleSet([]),
            ContentFingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sampleContent.ContentFingerprint + ":presentation-cue-test-v1"))),
        };
        var firstHost = HeadlessSystemicHost.Create(stableContent, seed: 84);
        var restoredHost = HeadlessSystemicHost.Restore(stableContent, firstHost.Save());
        firstHost.Publish(new SimulationFact("cue.start", oreId, oreId));
        restoredHost.Publish(new SimulationFact("cue.start", oreId, oreId));
        Assert.True(firstHost.Game.PresentationCues().TryDequeue(out var firstCue));
        Assert.True(restoredHost.Game.PresentationCues().TryDequeue(out var restoredCue));
        Assert.Equal(firstCue.VisualSeed, restoredCue.VisualSeed);
        using var savedDocument = System.Text.Json.JsonDocument.Parse(firstHost.Save());
        Assert.False(savedDocument.RootElement.TryGetProperty("presentationCues"u8, out _));
    }

    [Fact]
    public void Sample_pack_compiles_generic_presentation_bindings()
    {
        var content = SystemicTestContent.Load(SamplePath());

        Assert.Contains(SystemicTestContent.Presentation(content).Catalog.Bindings,
            binding => binding.EventType == "incremental.production");
        Assert.True(HeadlessSystemicHost.Create(content, seed: 85).Game.TryGetPresentationTimeline("quarry.impact", out _));
    }

    [Fact]
    public void Compiled_ui_pipeline_uses_current_provider_and_world_context_for_one_layout()
    {
        var sampleContent = SystemicTestContent.Load(SamplePath());
        var presentation = SystemicTestContent.Presentation(sampleContent);
        var layout = new UiLayout("binding-context", new UiNode("column", null, null, null,
        [
            new UiNode("value", null, "world.tick", null, []),
            new UiNode("value", null, "selection.energy", null, []),
            new UiNode("value", null, "module.owner", null, [])
        ]));
        var content = SystemicTestContent.WithPresentation(sampleContent,
            presentation with { Ui = new CompiledUi([layout]) });
        var materialized = content.CreateWorld(seed: 86, valueScale: 1);
        var provider = new OwnerBindingProvider();
        var game = new SystemicGameBuilder(content, materialized)
            .AddUiBindingProvider(provider)
            .Build();
        materialized.World.LogicalTick = 4;

        var first = game.EvaluateUi("binding-context", new Dictionary<string, double> { ["energy"] = 1 },
            choiceOwnerEntityId: 101);
        materialized.World.LogicalTick = 9;
        var second = game.EvaluateUi("binding-context", new Dictionary<string, double> { ["energy"] = 2 },
            choiceOwnerEntityId: 202);

        Assert.Equal(4, first.Children[0].NumberValue);
        Assert.Equal(1, first.Children[1].NumberValue);
        Assert.Equal(101, first.Children[2].NumberValue);
        Assert.Equal(9, second.Children[0].NumberValue);
        Assert.Equal(2, second.Children[1].NumberValue);
        Assert.Equal(202, second.Children[2].NumberValue);
        Assert.Equal([("module.owner", 101, 4), ("module.owner", 202, 9)], provider.Calls);
    }

    private static VisualRecipe Recipe(ImportedAssetMode importedMode) =>
        new("visual.hybrid", "actor", 11, 2, 1, ["#ff0000"],
            [new VisualLayer("rect", 0, 0, 0, 1, 1, 0)],
            ImportedAssetId: "asset.hero", ImportedMode: importedMode);

    private static PresentationCue Cue(string factType) =>
        new(0, 1, factType, "timeline", 1, 2, null, null, null, 0, 0, 0, 0);

    private static ContentPack ContentWithCueRules()
    {
        var content = SystemicTestContent.Load(SamplePath());
        var timeline = new PresentationTimeline("cue", "quarry.ember-spark", 8, false, 2,
            [new PresentationKeyframe(0)]);
        return SystemicTestContent.WithPresentation(content,
            SystemicTestContent.Presentation(content) with
            {
                Catalog = new PresentationCatalog([timeline], [
                new PresentationCueBinding("cue.start", "cue", PresentationAnchor.Target),
                new PresentationCueBinding("cue.chain", "cue", PresentationAnchor.Midpoint),
                ])
            }) with
        {
            Rules = new CompiledRuleSet([
                new CompiledRule("cue.start", "cue.start", 0, [], [RuleEffect.Emit("cue.chain")]),
                new CompiledRule("cue.chain", "cue.chain", 0, [],
                    [RuleEffect.AdjustProperty(RuleSubject.Target, "balance", 1)]),
            ]),
        };
    }

    private static string SamplePath() => Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry");

    private sealed class TestAssetProvider(IReadOnlyDictionary<string, PixelBuffer> assets) : IVisualAssetProvider
    {
        public bool TryGetPixels(string assetId, out PixelBuffer? pixels) => assets.TryGetValue(assetId, out pixels);
    }

    private sealed class OwnerBindingProvider : ISystemicUiBindingProvider
    {
        public List<(string Binding, int? OwnerId, int Tick)> Calls { get; } = [];

        public bool TryResolve(string binding, SystemicUiBindingContext context, out SystemicUiBindingValue value)
        {
            Calls.Add((binding, context.OwnerEntityId, context.World.LogicalTick));
            if (binding == "module.owner")
            {
                value = SystemicUiBindingValue.FromNumber(context.OwnerEntityId ?? -1);
                return true;
            }

            value = default;
            return false;
        }
    }
}
