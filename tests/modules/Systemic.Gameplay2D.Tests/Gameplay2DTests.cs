using Systemic.Kernel;
using Systemic.Content;
using Systemic.Extensions;
using Systemic.Gameplay2D;
using Systemic.Host;
using Systemic.Rules;
using Systemic.Save;

namespace Systemic.Gameplay2D.Tests;

public sealed class Gameplay2DTests
{
    [Fact]
    public void Spatial_target_selection_is_nearest_and_stable_on_ties()
    {
        var world = new World(seed: 1, valueScale: 1);
        var blueprint = Actor("target", new Dictionary<string, double>());
        var first = Gameplay2D.SpawnActor(world, blueprint, new Point2(5, 0));
        _ = Gameplay2D.SpawnActor(world, blueprint, new Point2(-5, 0));
        var candidates = new List<int>();

        var selected = Gameplay2D.SelectNearestTarget(world, new Point2(0, 0), 10, "target", candidates);

        Assert.Equal(first.Id, selected!.Id);
    }

    [Fact]
    public void Spatial_target_selection_can_choose_the_farthest_match_with_a_stable_tie_break()
    {
        var world = new World(seed: 18, valueScale: 1);
        _ = Gameplay2D.SpawnActor(world, Actor("target", new Dictionary<string, double>()), new Point2(2, 0));
        var firstFarthest = Gameplay2D.SpawnActor(world, Actor("target", new Dictionary<string, double>()), new Point2(7, 0));
        _ = Gameplay2D.SpawnActor(world, Actor("target", new Dictionary<string, double>()), new Point2(-7, 0));
        var candidates = new List<int>();

        var selected = Gameplay2D.SelectFarthestTarget(world, new Point2(0, 0), 10, "target", candidates);

        Assert.Equal(firstFarthest.Id, selected!.Id);
    }

    [Fact]
    public void Resource_transfer_is_atomic_and_respects_capacity()
    {
        var world = new World(seed: 2, valueScale: 1);
        var source = world.Add(new DataEntity([], [], ("ore", 8)));
        var target = world.Add(new DataEntity([], [], ("ore", 9), ("capacity", 10)));

        Assert.False(Gameplay2D.TryTransferResource(world, source.Id, target.Id, "ore", 2, "capacity"));
        Assert.True(source.TryGetProperty(world, "ore", out var sourceOre));
        Assert.True(target.TryGetProperty(world, "ore", out var targetOre));
        Assert.Equal(8, sourceOre);
        Assert.Equal(9, targetOre);

        Assert.True(Gameplay2D.TryTransferResource(world, source.Id, target.Id, "ore", 1, "capacity"));
        Assert.True(source.TryGetProperty(world, "ore", out sourceOre));
        Assert.True(target.TryGetProperty(world, "ore", out targetOre));
        Assert.Equal(7, sourceOre);
        Assert.Equal(10, targetOre);
    }

    [Fact]
    public void Resource_transfer_rejects_a_self_transfer_without_creating_resources()
    {
        var world = new World(seed: 17, valueScale: 1);
        var resource = world.Add(new DataEntity([], [], ("ore", 8)));

        var accepted = Gameplay2D.TryTransferResource(world, resource.Id, resource.Id, "ore", 3);

        Assert.False(accepted);
        Assert.True(resource.TryGetProperty(world, "ore", out var balance));
        Assert.Equal(8, balance);
    }

    [Fact]
    public void Projectiles_move_deterministically_and_expire_on_their_logical_tick()
    {
        var world = new World(seed: 3, valueScale: 1);
        var projectile = Gameplay2D.SpawnProjectile(world, Actor("bolt", new Dictionary<string, double>()),
            new Point2(0, 2), new System.Numerics.Vector2(3, -1), lifetimeTicks: 2);
        var system = new Gameplay2DModule();

        system.Step(Context(world, 1));
        Assert.True(projectile.TryGetProperty(world, Gameplay2D.PositionX, out var x));
        Assert.True(projectile.TryGetProperty(world, Gameplay2D.PositionY, out var y));
        Assert.Equal(3, x);
        Assert.Equal(1, y);
        Assert.False(projectile.ToRemove);

        system.Step(Context(world, 2));

        Assert.True(projectile.ToRemove);
        Assert.False(world.TryGetSpatialBounds(projectile.Id, out _));
    }

    [Fact]
    public void Out_of_range_projectile_motion_rolls_back_properties_and_spatial_bounds()
    {
        var world = new World(seed: 21, valueScale: 1);
        var projectile = Gameplay2D.SpawnProjectile(world, Actor("overflowing-bolt", new Dictionary<string, double>()),
            new Point2(0, 0), new System.Numerics.Vector2(0, 0), lifetimeTicks: 3);
        Assert.True(projectile.TrySetProperty(world, Gameplay2D.VelocityX, float.MaxValue));
        var system = new Gameplay2DModule();

        Assert.Throws<ArgumentOutOfRangeException>(() => system.Step(Context(world, 1)));

        Assert.True(projectile.TryGetProperty(world, Gameplay2D.PositionX, out var x));
        Assert.Equal(0, x);
        Assert.True(projectile.TryGetProperty(world, Gameplay2D.RemainingTicks, out var remainingTicks));
        Assert.Equal(3, remainingTicks);
        Assert.True(world.TryGetSpatialBounds(projectile.Id, out var bounds));
        Assert.Equal(new Bounds2(0, 0, 1, 1), bounds);
    }

    [Fact]
    public void Projectile_with_unindexable_trajectory_is_rejected_before_it_can_poison_game_ticks()
    {
        var world = new World(seed: 211, valueScale: 1);
        var content = Systemic.Content.ContentPackLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "samples", "systemic-sandbox"));
        var game = new SystemicGameBuilder(content, world).Use<Gameplay2DModule>().Build();

        Assert.Throws<ArgumentOutOfRangeException>(() => Gameplay2D.SpawnProjectile(world,
            Actor("unindexable-bolt", new Dictionary<string, double>()), new Point2(0, 0),
            new System.Numerics.Vector2(1e20f, 0), lifetimeTicks: 3));
        Assert.Empty(world.Entities);
        Assert.Equal(0, world.SpatialEntityCount);

        game.Step();

        Assert.Equal(1, world.LogicalTick);
    }

    [Fact]
    public void Stackable_statuses_refresh_expire_and_restore_effective_properties()
    {
        var world = new World(seed: 4, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("power", 10)));
        var status = new StatusEffectDefinition("focus", "focused", "power", ModifierOperation.Add,
            5, DurationTicks: 2, Stacking: StatusStackingMode.Stack, MaximumStacks: 2);

        Assert.Equal(1, Gameplay2D.ApplyStatus(world, target, status));
        Assert.Equal(2, Gameplay2D.ApplyStatus(world, target, status));
        Assert.Equal(2, Gameplay2D.ApplyStatus(world, target, status));
        Assert.Equal(20, world.EffectiveProperty(target, "power"));
        Assert.True(world.HasTag(target, "focused"));

        var system = new Gameplay2DModule();
        world.LogicalTick = 1;
        system.Step(Context(world, 1));
        world.AdvanceModifiers();
        world.LogicalTick = 2;
        system.Step(Context(world, 2));
        world.AdvanceModifiers();

        Assert.Equal(10, world.EffectiveProperty(target, "power"));
        Assert.False(world.HasTag(target, "focused"));
    }

    [Fact]
    public void Status_that_overflows_an_effective_property_is_rejected_atomically()
    {
        var world = new World(seed: 22, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("power", 2)));
        var status = new StatusEffectDefinition("overflow", "status.overflow", "power",
            ModifierOperation.Multiply, double.MaxValue, DurationTicks: 4);

        Assert.Throws<InvalidOperationException>(() => Gameplay2D.ApplyStatus(world, target, status));

        Assert.Equal(2, world.EffectiveProperty(target, "power"));
        Assert.Empty(world.Modifiers);
        Assert.False(world.HasTag(target, "status.overflow"));
    }

    [Fact]
    public void Modifier_durations_advance_in_the_host_without_the_optional_2d_module()
    {
        var content = Systemic.Content.ContentPackLoader.Load(
            Path.Combine(AppContext.BaseDirectory, "samples", "systemic-sandbox"));
        var world = new World(seed: 19, valueScale: 1);
        var target = world.Add(new DataEntity([], [], ("power", 10)));
        Gameplay2D.ApplyStatus(world, target, new StatusEffectDefinition("focus", "focused", "power",
            ModifierOperation.Add, 5, DurationTicks: 2));
        var game = new SystemicGameBuilder(content, world).Build();

        game.Step();
        Assert.Equal(15, world.EffectiveProperty(target, "power"));
        game.Step();

        Assert.Equal(10, world.EffectiveProperty(target, "power"));
        Assert.False(world.HasTag(target, "focused"));
    }

    [Fact]
    public void Gameplay_module_can_be_registered_as_one_systemic_extension()
    {
        var builder = new SystemicGameBuilder(
            Systemic.Content.ContentPackLoader.Load(
                Path.Combine(AppContext.BaseDirectory, "samples", "systemic-sandbox")),
            Systemic.Content.ContentPackLoader.Load(
                Path.Combine(AppContext.BaseDirectory, "samples", "systemic-sandbox")).CreateWorld(5, 1));
        builder.AddExtension(new Gameplay2DModule());

        var game = builder.Build();
        game.Step();

        Assert.Equal(1, game.World.LogicalTick);
    }

    [Fact]
    public void Saved_projectile_motion_and_spatial_bounds_resume_deterministically()
    {
        var content = ContentPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "samples", "systemic-sandbox"));
        var initial = content.CreateWorld(seed: 20, valueScale: 1);
        var world = initial.World;
        var projectile = Gameplay2D.SpawnProjectile(world, Actor("bolt", new Dictionary<string, double>()),
            new Point2(1, 2), new System.Numerics.Vector2(3, -1), lifetimeTicks: 4);
        var game = new SystemicGameBuilder(content, initial).AddExtension(new Gameplay2DModule()).Build();
        game.Step();

        var codec = new SystemicSaveCodec();
        var loaded = codec.Load(content, codec.Save(game));
        var restored = new SystemicGameBuilder(content, loaded).AddExtension(new Gameplay2DModule()).Build();
        var restoredProjectile = restored.World.ById(projectile.Id)!;
        Assert.True(restored.World.TryGetSpatialBounds(restoredProjectile.Id, out var restoredBounds));
        Assert.Equal(new Bounds2(4, 1, 1, 1), restoredBounds);

        game.Step();
        restored.Step();

        Assert.True(projectile.TryGetProperty(game.World, Gameplay2D.PositionX, out var expectedX));
        Assert.True(restoredProjectile.TryGetProperty(restored.World, Gameplay2D.PositionX, out var actualX));
        Assert.Equal(expectedX, actualX);
        Assert.True(restored.World.TryGetSpatialBounds(restoredProjectile.Id, out restoredBounds));
        Assert.Equal(new Bounds2(7, 0, 1, 1), restoredBounds);
    }

    private static ActorDefinition Actor(string id, IReadOnlyDictionary<string, double> properties) =>
        new(id, ["target"], [], properties, 1, 1);

    private static SystemicSystemContext Context(World world, int tick) =>
        new(world, new RuleEngine(new CompiledRuleSet([])), new EngineBuilder().Build(), tick);
}
