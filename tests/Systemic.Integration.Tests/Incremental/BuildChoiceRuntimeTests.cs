using System.Collections.Immutable;
using System.Text.Json;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;
using Systemic.Incremental;
using Systemic.Rules;
using Systemic.Save;
using Systemic.Tests;

namespace Systemic.Tests.Incremental;

public sealed class BuildChoiceRuntimeTests
{
    [Fact]
    public void Conflict_indexes_preserve_self_directional_and_reciprocal_exclusions()
    {
        var (world, runtime, primaryOwnerId) = CreateChoiceRuntime(
            ("self", ["self"]),
            ("directional.first", ["directional.second"]),
            ("directional.second", []),
            ("reciprocal.first", ["reciprocal.second"]),
            ("reciprocal.second", ["reciprocal.first"]));
        var publish = new Func<SimulationFact, RuleDispatchResult>(_ => new RuleDispatchResult(1, 0, 0, false));

        Assert.True(runtime.SelectChoice("self", primaryOwnerId, publish).Accepted);

        var directionalOwner = AddAndRegisterOwner(world, runtime);
        Assert.True(runtime.SelectChoice("directional.second", directionalOwner, publish).Accepted);
        Assert.True(runtime.TryGetState("directional.first", directionalOwner, out var directionallyExcluded));
        Assert.False(directionallyExcluded.IsAvailable);
        Assert.False(runtime.SelectChoice("directional.first", directionalOwner, publish).Accepted);

        var reciprocalFirstOwner = AddAndRegisterOwner(world, runtime);
        Assert.True(runtime.SelectChoice("reciprocal.first", reciprocalFirstOwner, publish).Accepted);
        Assert.True(runtime.TryGetState("reciprocal.second", reciprocalFirstOwner, out var reciprocalSecond));
        Assert.False(reciprocalSecond.IsAvailable);

        var reciprocalSecondOwner = AddAndRegisterOwner(world, runtime);
        Assert.True(runtime.SelectChoice("reciprocal.second", reciprocalSecondOwner, publish).Accepted);
        Assert.True(runtime.TryGetState("reciprocal.first", reciprocalSecondOwner, out var reciprocalFirst));
        Assert.False(reciprocalFirst.IsAvailable);
    }

    [Fact]
    public void Owner_membership_and_capture_order_remain_stable()
    {
        using var pack = CreateChoicePack();
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 52, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var runtime = game.BuildChoices()!;
        var owners = new[]
        {
            materialized.World.Add(new DataEntity([], [], ("progress", 0))),
            materialized.World.Add(new DataEntity([], [], ("progress", 0))),
            materialized.World.Add(new DataEntity([], [], ("progress", 0)))
        };
        foreach (var owner in owners.Reverse())
        {
            Assert.True(runtime.RegisterOwner("tier-one", owner.Id, owner.Id).Accepted);
        }

        Assert.False(runtime.RegisterOwner("tier-one", owners[1].Id, owners[1].Id).Accepted);
        var captured = runtime.CaptureState().Deserialize<BuildChoiceRuntimeState>()!;

        Assert.Equal(owners.Select(owner => owner.Id), captured.Owners.Select(owner => owner.OwnerEntityId));
        Assert.All(captured.Owners, owner => Assert.Equal(
            owner.Choices.Select(choice => choice.ChoiceId).Order(StringComparer.Ordinal),
            owner.Choices.Select(choice => choice.ChoiceId)));
    }

    [Fact]
    public void Choice_state_reads_live_world_values_after_external_mutation_and_rollback()
    {
        using var pack = CreateChoicePack();
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 53, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var runtime = game.BuildChoices()!;
        var choice = materialized.World.ById(materialized.EntityIds["choice.first"])!;

        using (var transaction = materialized.World.BeginTransaction())
        {
            Assert.True(choice.TrySetProperty(materialized.World, "selected", 1));
            Assert.True(runtime.TryGetState("choice.first", out var selected));
            Assert.True(selected.IsSelected);
            transaction.Rollback();
        }

        Assert.True(runtime.TryGetState("choice.first", out var restored));
        Assert.False(restored.IsSelected);
    }

    [Fact]
    public void Free_choice_is_always_selected_and_does_not_use_a_selectable_slot()
    {
        using var pack = CreateChoicePack();
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 12, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var ownerId = materialized.EntityIds["actor.owner"];
        var ore = materialized.World.ById(materialized.EntityIds["resource.ore"])!;

        game.Step();
        Assert.True(materialized.World.HasTag(materialized.World.ById(ownerId)!, "choice.permanent"));
        var first = game.SelectChoice("choice.first", ownerId);
        var second = game.SelectChoice("choice.second", ownerId);

        Assert.True(first.Accepted);
        Assert.False(second.Accepted);
        Assert.True(ore.TryGetProperty(materialized.World, "balance", out var balance));
        Assert.Equal(9, balance);
    }

    [Fact]
    public void Choice_commands_require_the_group_owner_and_do_not_spend_for_other_actors()
    {
        using var pack = CreateChoicePack();
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 12, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var ore = materialized.World.ById(materialized.EntityIds["resource.ore"])!;

        var result = game.SelectChoice("choice.first", purchaserId: 0);

        Assert.False(result.Accepted);
        Assert.True(ore.TryGetProperty(materialized.World, "balance", out var balance));
        Assert.Equal(10, balance);
        Assert.False(materialized.World.HasTag(materialized.World.ById(materialized.EntityIds["actor.owner"])!, "choice.first"));
    }

    [Fact]
    public void Timed_choice_exposes_active_and_cooldown_state_and_expires_deterministically()
    {
        using var pack = CreateChoicePack();
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 12, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var ownerId = materialized.EntityIds["actor.owner"];
        var choice = materialized.World.ById(materialized.EntityIds["choice.second"])!;

        game.Step();
        Assert.True(game.SelectChoice("choice.second", ownerId).Accepted);
        Assert.True(game.ActivateChoice("choice.second", ownerId).Accepted);
        Assert.True(materialized.World.HasTag(choice, "choice.second.active"));
        Assert.True(game.BuildChoices()!.TryGetState("choice.second", out var activated));
        Assert.True(activated.IsActive);
        Assert.Equal(0, activated.CooldownProgress);
        var ui = game.EvaluateUi("hud");
        Assert.Equal("spark", ui.Children[0].VisualRecipeId);
        Assert.Equal("active", ui.Children[0].VisualState);
        Assert.Equal(1, ui.Children[0].NumberValue);
        Assert.Equal(0, ui.Children[1].Progress);
        Assert.True(choice.TryGetProperty(materialized.World, "activeUntilTick", out var activeUntil));
        Assert.Equal(3, activeUntil);
        Assert.False(game.ActivateChoice("choice.second", ownerId).Accepted);

        game.Step();
        Assert.True(materialized.World.HasTag(choice, "choice.second.active"));
        game.Step();
        Assert.False(materialized.World.HasTag(choice, "choice.second.active"));
        Assert.True(game.BuildChoices().TryGetState("choice.second", out var cooling));
        Assert.Equal(0, cooling.CooldownProgress);
        game.Step();
        Assert.True(game.BuildChoices().TryGetState("choice.second", out var recharging));
        Assert.Equal(1.0 / 3.0, recharging.CooldownProgress, precision: 10);
    }

    [Fact]
    public void Triggered_choice_emits_a_separate_bounded_activation_fact()
    {
        using var pack = CreateChoicePack();
        var content = SystemicTestContent.Load(pack.Root) with
        {
            Rules = new CompiledRuleSet([
                new CompiledRule("choice.activation", "choice.third.activated", 0, [],
                    [RuleEffect.AdjustProperty(RuleSubject.Target, "activations", 1)])
            ])
        };
        var materialized = content.CreateWorld(seed: 12, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var ownerId = materialized.EntityIds["actor.owner"];
        var choice = materialized.World.ById(materialized.EntityIds["choice.third"])!;

        game.Step();
        Assert.True(game.SelectChoice("choice.third", ownerId).Accepted);
        var result = game.Publish(new SimulationFact("incoming.spark", ownerId, ownerId));

        Assert.Equal(2, result.FactsProcessed);
        Assert.True(materialized.World.HasTag(choice, "choice.third.active"));
        Assert.True(choice.TryGetProperty(materialized.World, "activations", out var activations));
        Assert.Equal(1, activations);
    }

    [Fact]
    public void Nested_prerequisites_and_exclusions_control_choice_availability()
    {
        using var pack = CreateChoicePack();
        var advancementPath = Path.Combine(pack.Root, "advancements.json");
        var advancements = File.ReadAllText(advancementPath)
            .Replace("\"selectionLimit\": 1", "\"selectionLimit\": 4", StringComparison.Ordinal)
            .Replace("\"costResourceId\": \"ore\", \"costAmount\": 1, \"activeTicks\": 2,",
                "\"requires\": [\"choice.first\"], \"costResourceId\": \"ore\", \"costAmount\": 1, \"activeTicks\": 2,",
                StringComparison.Ordinal)
            .Replace("\"triggerEvent\": \"incoming.spark\",",
                "\"requiresAny\": [\"choice.first\", \"choice.second\"], \"excludes\": [\"choice.second\"], \"triggerEvent\": \"incoming.spark\",",
                StringComparison.Ordinal);
        File.WriteAllText(advancementPath, advancements);
        var uiPath = Path.Combine(pack.Root, "ui.json");
        File.WriteAllText(uiPath, File.ReadAllText(uiPath)
            .Replace("choice.choice.second.active", "choice.choice.second.available", StringComparison.Ordinal));

        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 12, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var ownerId = materialized.EntityIds["actor.owner"];

        Assert.True(game.BuildChoices()!.TryGetState("choice.second", out var initiallyLocked));
        Assert.True(initiallyLocked.IsLocked);
        Assert.False(initiallyLocked.IsAvailable);
        Assert.Equal(0, game.EvaluateUi("hud").Children[0].NumberValue);
        Assert.False(game.SelectChoice("choice.second", ownerId).Accepted);

        Assert.True(game.SelectChoice("choice.first", ownerId).Accepted);
        Assert.True(game.BuildChoices().TryGetState("choice.second", out var nowAvailable));
        Assert.False(nowAvailable.IsLocked);
        Assert.True(nowAvailable.IsAvailable);
        Assert.Equal(1, game.EvaluateUi("hud").Children[0].NumberValue);
        Assert.True(game.SelectChoice("choice.second", ownerId).Accepted);

        Assert.True(game.BuildChoices().TryGetState("choice.third", out var excluded));
        Assert.False(excluded.IsAvailable);
        Assert.False(game.SelectChoice("choice.third", ownerId).Accepted);
    }

    [Fact]
    public void Alternative_prerequisite_can_break_a_required_choice_cycle()
    {
        using var pack = CreateChoicePack();
        var path = Path.Combine(pack.Root, "advancements.json");
        var advancements = File.ReadAllText(path)
            .Replace("\"selectionLimit\": 1", "\"selectionLimit\": 4", StringComparison.Ordinal)
            .Replace("\"choiceTag\": \"choice.first\"", "\"choiceTag\": \"choice.first\", \"requiresAny\": [\"choice.second\", \"choice.third\"]", StringComparison.Ordinal)
            .Replace("\"choiceTag\": \"choice.second\"", "\"choiceTag\": \"choice.second\", \"requires\": [\"choice.first\"]", StringComparison.Ordinal);
        File.WriteAllText(path, advancements);

        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 22, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var ownerId = materialized.EntityIds["actor.owner"];

        Assert.True(game.BuildChoices()!.TryGetState("choice.third", out var bootstrap));
        Assert.True(bootstrap.IsAvailable);
        Assert.True(game.BuildChoices().TryGetState("choice.first", out var gated));
        Assert.True(gated.IsLocked);
        Assert.True(game.SelectChoice("choice.third", ownerId).Accepted);
        Assert.True(game.BuildChoices().TryGetState("choice.first", out var unlocked));
        Assert.True(unlocked.IsAvailable);
        Assert.True(game.SelectChoice("choice.first", ownerId).Accepted);
        Assert.True(game.BuildChoices().TryGetState("choice.second", out var dependent));
        Assert.True(dependent.IsAvailable);
    }

    [Fact]
    public void Pure_prerequisite_cycles_are_rejected_as_unselectable()
    {
        using var pack = CreateChoicePack();
        var path = Path.Combine(pack.Root, "advancements.json");
        var advancements = File.ReadAllText(path)
            .Replace("\"choiceTag\": \"choice.first\"", "\"choiceTag\": \"choice.first\", \"requires\": [\"choice.second\"]", StringComparison.Ordinal)
            .Replace("\"choiceTag\": \"choice.second\"", "\"choiceTag\": \"choice.second\", \"requires\": [\"choice.first\"]", StringComparison.Ordinal);
        File.WriteAllText(path, advancements);

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains("prerequisite cycle", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Prerequisite_chains_must_fit_the_group_selection_limit()
    {
        using var pack = CreateChoicePack();
        var path = Path.Combine(pack.Root, "advancements.json");
        File.WriteAllText(path, File.ReadAllText(path)
            .Replace("\"triggerEvent\": \"incoming.spark\"",
                "\"requires\": [\"choice.first\"], \"triggerEvent\": \"incoming.spark\"", StringComparison.Ordinal));

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.File == "game.json"
            && diagnostic.Path == "$.modules.systemic.incremental.choices[3].requires"
            && diagnostic.Message.Contains("selection limit", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("selection limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transitive_prerequisite_chains_must_fit_the_group_selection_limit()
    {
        using var pack = CreateChoicePack();
        var path = Path.Combine(pack.Root, "advancements.json");
        File.WriteAllText(path, File.ReadAllText(path)
            .Replace("\"selectionLimit\": 1", "\"selectionLimit\": 2", StringComparison.Ordinal)
            .Replace("\"choiceTag\": \"choice.second\",", "\"choiceTag\": \"choice.second\", \"requires\": [\"choice.first\"],", StringComparison.Ordinal)
            .Replace("\"triggerEvent\": \"incoming.spark\"", "\"requires\": [\"choice.second\"], \"triggerEvent\": \"incoming.spark\"", StringComparison.Ordinal));

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.File == "game.json"
            && diagnostic.Path == "$.modules.systemic.incremental.choices[3].requires"
            && diagnostic.Message.Contains("selection limit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Transitive_prerequisites_must_not_conflict_with_the_choice()
    {
        using var pack = CreateChoicePack();
        var path = Path.Combine(pack.Root, "advancements.json");
        File.WriteAllText(path, File.ReadAllText(path)
            .Replace("\"selectionLimit\": 1", "\"selectionLimit\": 3", StringComparison.Ordinal)
            .Replace("\"choiceTag\": \"choice.first\"", "\"choiceTag\": \"choice.first\", \"excludes\": [\"choice.third\"]", StringComparison.Ordinal)
            .Replace("\"choiceTag\": \"choice.second\",", "\"choiceTag\": \"choice.second\", \"requires\": [\"choice.first\"],", StringComparison.Ordinal)
            .Replace("\"triggerEvent\": \"incoming.spark\"", "\"requires\": [\"choice.second\"], \"triggerEvent\": \"incoming.spark\"", StringComparison.Ordinal));

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.File == "game.json"
            && diagnostic.Path == "$.modules.systemic.incremental.choices[3].requires");
    }

    [Fact]
    public void Selection_limit_counts_a_prerequisites_alternative_unlock_chain()
    {
        using var pack = CreateChoicePack();
        var path = Path.Combine(pack.Root, "advancements.json");
        File.WriteAllText(path, File.ReadAllText(path)
            .Replace("\"selectionLimit\": 1", "\"selectionLimit\": 2", StringComparison.Ordinal)
            .Replace("\"choiceTag\": \"choice.first\"", "\"choiceTag\": \"choice.first\", \"requiresAny\": [\"choice.third\"]", StringComparison.Ordinal)
            .Replace("\"choiceTag\": \"choice.second\",", "\"choiceTag\": \"choice.second\", \"requires\": [\"choice.first\"],", StringComparison.Ordinal));

        var error = Assert.Throws<ContentPackException>(() => SystemicTestContent.Load(pack.Root));

        Assert.Contains(error.Diagnostics, diagnostic => diagnostic.File == "game.json"
            && diagnostic.Path == "$.modules.systemic.incremental.choices[2].requires");
    }

    [Fact]
    public void Choice_ownership_is_independent_per_actor_and_survives_save_restore()
    {
        using var pack = CreateChoicePack();
        var uiPath = Path.Combine(pack.Root, "ui.json");
        File.WriteAllText(uiPath, File.ReadAllText(uiPath)
            .Replace("choice.choice.second.active", "choice.choice.first.selected", StringComparison.Ordinal));
        var content = SystemicTestContent.Load(pack.Root);
        var materialized = content.CreateWorld(seed: 23, valueScale: 1);
        var game = new SystemicGameBuilder(content, materialized).Build();
        var primaryOwnerId = materialized.EntityIds["actor.owner"];
        game.Step();
        var secondaryOwner = materialized.World.Add(new DataEntity([], [], ("progress", -1)));

        Assert.True(game.RegisterChoiceOwner("tier-one", secondaryOwner.Id, secondaryOwner.Id).Accepted);
        Assert.True(game.BuildChoices()!.TryGetState("choice.first", secondaryOwner.Id, out var secondaryLocked));
        Assert.True(secondaryLocked.IsLocked);
        Assert.True(secondaryOwner.TrySetProperty(materialized.World, "progress", 0));
        Assert.True(game.SelectChoice("choice.first", secondaryOwner.Id).Accepted);
        Assert.True(game.BuildChoices().TryGetState("choice.first", secondaryOwner.Id, out var secondaryState));
        Assert.True(secondaryState.IsSelected);
        Assert.True(game.BuildChoices().TryGetState("choice.first", out var primaryState));
        Assert.False(primaryState.IsSelected);
        Assert.True(game.SelectChoice("choice.first", primaryOwnerId).Accepted);
        Assert.Equal(1, game.EvaluateUi("hud", choiceOwnerEntityId: secondaryOwner.Id).Children[0].NumberValue);
        Assert.Equal(1, game.EvaluateUi("hud").Children[0].NumberValue);

        var restoredMaterialized = new SystemicSaveCodec().Load(content, new SystemicSaveCodec().Save(game));
        var restoredGame = new SystemicGameBuilder(content, restoredMaterialized).Build();

        Assert.True(restoredGame.BuildChoices()!.TryGetState("choice.first", secondaryOwner.Id, out var restoredSecondary));
        Assert.True(restoredSecondary.IsSelected);
        Assert.True(restoredGame.BuildChoices().TryGetState("choice.first", primaryOwnerId, out var restoredPrimary));
        Assert.True(restoredPrimary.IsSelected);
    }

    private static TestPack CreateChoicePack()
    {
        var pack = TestPack.Create();
        var gamePath = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(gamePath, File.ReadAllText(gamePath)
            .Replace("\"ui\": \"ui.json\"", "\"ui\": \"ui.json\",\n  \"incremental\": \"economy.json\",\n  \"advancements\": \"advancements.json\"", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(pack.Root, "entities.json"), """
            { "schemaVersion": 2, "entities": [
              { "id": "resource.ore", "kind": "resource", "tags": ["resource"], "properties": { "balance": 0 } },
              { "id": "actor.owner", "kind": "actor", "properties": { "progress": 0 } },
              { "id": "choice.permanent", "kind": "prop", "properties": { "selected": 0 } },
              { "id": "choice.first", "kind": "prop", "properties": { "selected": 0 } },
              { "id": "choice.second", "kind": "prop",
                "properties": { "selected": 0, "activeUntilTick": 0, "cooldownUntilTick": 0 } },
              { "id": "choice.third", "kind": "prop",
                "properties": { "selected": 0, "activeUntilTick": 0, "cooldownUntilTick": 0, "activations": 0 } }
            ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "economy.json"), """
            { "schemaVersion": 2, "resources": [
              { "id": "ore", "entityId": "resource.ore", "initialValue": 10 }
            ], "producers": [], "upgrades": [], "roles": [] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "advancements.json"), """
            { "schemaVersion": 2,
              "groups": [{ "id": "tier-one", "ownerEntityId": "actor.owner",
                "progressEntityId": "actor.owner", "progressProperty": "progress",
                "unlockAt": 0, "selectionLimit": 1, "freeChoiceId": "choice.permanent" }],
              "choices": [
                { "id": "choice.permanent", "groupId": "tier-one", "entityId": "choice.permanent",
                  "activationMode": "passive", "isFree": true, "choiceTag": "choice.permanent" },
                { "id": "choice.first", "groupId": "tier-one", "entityId": "choice.first",
                  "activationMode": "passive", "choiceTag": "choice.first",
                  "costResourceId": "ore", "costAmount": 1 },
                { "id": "choice.second", "groupId": "tier-one", "entityId": "choice.second",
                  "activationMode": "timed", "choiceTag": "choice.second",
                  "costResourceId": "ore", "costAmount": 1, "activeTicks": 2,
                  "cooldownTicks": 3, "activationEvent": "choice.second.activated" },
                { "id": "choice.third", "groupId": "tier-one", "entityId": "choice.third",
                  "activationMode": "triggered", "choiceTag": "choice.third",
                  "triggerEvent": "incoming.spark", "activationEvent": "choice.third.activated",
                  "activeTicks": 1, "cooldownTicks": 2 }
              ] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "ui.json"), """
            { "schemaVersion": 2, "layouts": [{ "id": "hud", "root": { "type": "column", "children": [
              { "type": "icon", "visualRecipeId": "spark", "bind": "choice.choice.second.active" },
              { "type": "bar", "bind": "choice.choice.second.cooldown" }
            ] } }] }
            """);
        File.WriteAllText(Path.Combine(pack.Root, "presentation.json"), """
            { "schemaVersion": 2, "timelines": [
              { "id": "spark.effect", "visualRecipeId": "spark", "durationTicks": 1, "loop": false,
                "particleCount": 0, "keyframes": [{ "tick": 0 }] }
            ], "bindings": [], "choiceIcons": {
              "choice.permanent": "spark", "choice.first": "spark", "choice.second": "spark", "choice.third": "spark"
            } }
            """);
        return pack;
    }

    private static (World World, BuildChoiceRuntime Runtime, int PrimaryOwnerId) CreateChoiceRuntime(
        params (string Id, string[] Excludes)[] choiceSpecs)
    {
        var world = new World(seed: 54, valueScale: 1);
        var entityIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var owner = world.Add(new DataEntity([], [], ("progress", 0)));
        entityIds.Add("owner.primary", owner.Id);
        var choices = ImmutableArray.CreateBuilder<BuildChoiceDefinition>(choiceSpecs.Length);
        foreach (var (id, excludes) in choiceSpecs)
        {
            var entityId = $"entity.{id}";
            var entity = world.Add(new DataEntity([], [], ("selected", 0)));
            entityIds.Add(entityId, entity.Id);
            choices.Add(new BuildChoiceDefinition(id, "group.main", entityId,
                ChoiceActivationMode.Passive, $"tag.{id}", "selected", "activeUntil", "cooldownUntil",
                null, 0, 0, 0, null, null, false, Excludes: [.. excludes]));
        }

        var catalog = new BuildChoiceCatalog(
            [new BuildChoiceGroupDefinition("group.main", "owner.primary", "owner.primary", "progress", 0, 8, null)],
            choices.MoveToImmutable());
        var runtime = new BuildChoiceRuntime(catalog, null, world, entityIds);
        return (world, runtime, owner.Id);
    }

    private static int AddAndRegisterOwner(World world, BuildChoiceRuntime runtime)
    {
        var owner = world.Add(new DataEntity([], [], ("progress", 0)));
        Assert.True(runtime.RegisterOwner("group.main", owner.Id, owner.Id).Accepted);
        return owner.Id;
    }
}
