using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class ModifierPrimitiveTests
{
    [Fact]
    public void Min_and_max_modifiers_clamp_the_effective_value()
    {
        var world = new World(seed: 1, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("value", 2)));

        world.SetModifier("floor", entity, "value", ModifierOperation.Max, 5);
        Assert.Equal(5, world.EffectiveProperty(entity, "value"));

        world.SetModifier("cap", entity, "value", ModifierOperation.Min, 4);
        Assert.Equal(4, world.EffectiveProperty(entity, "value"));
    }

    [Fact]
    public void Two_sources_stack_and_one_source_replaces_only_itself()
    {
        var world = new World(seed: 2, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("value", 10)));

        world.SetModifier("aura", entity, "value", ModifierOperation.Add, 2);
        world.SetModifier("blessing", entity, "value", ModifierOperation.Add, 3);
        Assert.Equal(15, world.EffectiveProperty(entity, "value"));

        world.SetModifier("aura", entity, "value", ModifierOperation.Add, 5);
        Assert.Equal(18, world.EffectiveProperty(entity, "value"));
        Assert.Equal(2, world.Modifiers.Count);
    }

    [Fact]
    public void A_rank_aware_modifier_reads_the_rank_property_at_read_time()
    {
        var world = new World(seed: 3, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("rank", 3), ("rate", 2)));

        world.SetModifier("upgrade", entity, "rate", ModifierOperation.Add, 1, sourceProperty: "rank");
        Assert.Equal(5, world.EffectiveProperty(entity, "rate"));

        Assert.True(entity.TrySetProperty(world, "rank", 5));
        Assert.Equal(7, world.EffectiveProperty(entity, "rate"));
    }

    [Fact]
    public void A_rank_aware_modifier_with_a_missing_rank_contributes_nothing()
    {
        var world = new World(seed: 4, valueScale: 1);
        var entity = world.Add(new DataEntity([], [], ("rate", 2)));

        world.SetModifier("upgrade", entity, "rate", ModifierOperation.Add, 1, sourceProperty: "rank");

        Assert.Equal(2, world.EffectiveProperty(entity, "rate"));
    }

    [Fact]
    public void A_timed_rank_aware_modifier_disappears_with_its_duration()
    {
        var world = new World(seed: 5, valueScale: 1);
        world.LogicalTick = 1;
        var entity = world.Add(new DataEntity([], [], ("rank", 2), ("rate", 2)));

        world.SetModifier("buff", entity, "rate", ModifierOperation.Add, 1, sourceProperty: "rank",
            remainingTicks: 2);
        Assert.Equal(4, world.EffectiveProperty(entity, "rate"));

        world.LogicalTick = 3;
        world.AdvanceModifiers();
        Assert.Empty(world.Modifiers);
        Assert.Equal(2, world.EffectiveProperty(entity, "rate"));
    }
}
