using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;
using Systemic.Rules;
using Systemic.Ui;

namespace Systemic.Tests.Presentation;

public sealed class PresentationPipelineTests
{
    [Fact]
    public void ImportedPixelsComposeWithGeneratedLayersAndMissingAssetsFallBack()
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
    public void TimelineKeyframesInterpolateMotionAndStopAtDuration()
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
    public void CueStreamOrdersEntriesAndDropsTheOldestWithACounter()
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
    public void CueStreamRejectsInvalidMetadataWithoutConsumingASequence()
    {
        var stream = new PresentationCueStream(capacity: 2);

        Assert.Throws<ArgumentException>(() => stream.Enqueue(Cue("broken") with { Amount = double.NaN }));
        Assert.Throws<ArgumentException>(() => stream.Enqueue(Cue("broken") with { Tick = -1 }));
        Assert.Equal(1, stream.Enqueue(Cue("valid")));
        Assert.Equal(0, stream.DroppedCount);
    }

    [Fact]
    public void CuePublicationIsOrderedPostCommitAndUsesPositionSnapshots()
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
    public void ExplicitFactPositionAnchorsPresentationAndEmittedReactions()
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
    public void CuePublicationWaitsForTheEnclosingWorldTransaction()
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
    public void CuePublicationIsDiscardedWhenTheEnclosingWorldTransactionRollsBack()
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
    public void InvalidFactMetadataIsRejectedBeforeRuleEffectsCommit(bool negativeSourceId)
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
    public void RolledBackRulesSuppressCuesAndCueSeedsSurviveSaveRestore()
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
    public void SamplePackCompilesGenericPresentationBindings()
    {
        var content = SystemicTestContent.Load(SamplePath());

        Assert.Contains(SystemicTestContent.Presentation(content).Catalog.Bindings,
            binding => binding.EventType == "incremental.production");
        Assert.True(HeadlessSystemicHost.Create(content, seed: 85).Game.TryGetPresentationTimeline("quarry.impact", out _));
    }

    [Fact]
    public void CompiledUiPipelineUsesCurrentProviderAndWorldContextForOneLayout()
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

    [Fact]
    public void A_choice_list_and_an_upgrade_list_share_the_same_widget_template()
    {
        var world = new World(seed: 91, valueScale: 1);
        world.Add(new DataEntity(["ui.choice"], [], ("cost", 5), ("progress", 0.5)));
        world.Add(new DataEntity(["ui.choice"], [], ("cost", 7), ("progress", 0.25)));
        world.Add(new DataEntity(["ui.upgrade"], [], ("cost", 9), ("progress", 0.75)));
        var layout = new UiLayout("lists", new UiNode("column", null, null, null,
        [
            ItemList("ui.choice"),
            ItemList("ui.upgrade")
        ]));

        var state = DeclarativeUiEvaluator.Evaluate(layout, new UiBindingContext(world, new Dictionary<string, int>()));

        var choices = state.Children[0];
        var upgrades = state.Children[1];
        Assert.Equal(2, choices.Children.Length);
        Assert.Single(upgrades.Children);
        Assert.All(choices.Children, item => Assert.Equal(item.Children[0].Type, upgrades.Children[0].Children[0].Type));
        Assert.Equal(5, choices.Children[0].Children[0].NumberValue);
        Assert.Equal(7, choices.Children[1].Children[0].NumberValue);
        Assert.Equal(0.5, choices.Children[0].Children[1].Progress);
        Assert.Equal(0.25, choices.Children[1].Children[1].Progress);
        Assert.Equal(9, upgrades.Children[0].Children[0].NumberValue);
        Assert.Equal(0.75, upgrades.Children[0].Children[1].Progress);
    }

    private static UiNode ItemList(string tag) =>
        new("list", null, $"list.tag:{tag}", null,
        [
            new UiNode("row", null, null, null,
            [
                new UiNode("value", null, "item.cost", null, []),
                new UiNode("bar", null, "item.progress", null, [], 0, 1)
            ])
        ]);

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
