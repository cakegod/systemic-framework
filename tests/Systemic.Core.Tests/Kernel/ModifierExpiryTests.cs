using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class ModifierExpiryTests
{
    [Fact]
    public void TimedModifierExpiresAtTheEndOfItsDuration()
    {
        var world = new World(seed: 1, valueScale: 1);
        world.LogicalTick = 1;
        var entity = world.Add(new DataEntity([], [], ("value", 0.0)));

        world.SetModifier("source", entity, "value", ModifierOperation.Add, 2, remainingTicks: 3);

        var modifier = Assert.Single(world.Modifiers);
        Assert.Equal(3, modifier.ExpiryTick);
        Assert.True(modifier.IsActiveAt(3));

        world.AdvanceModifiers();
        Assert.Single(world.Modifiers);

        world.LogicalTick = 2;
        world.AdvanceModifiers();
        Assert.Single(world.Modifiers);

        world.LogicalTick = 3;
        world.AdvanceModifiers();
        Assert.Empty(world.Modifiers);
        Assert.Equal(0, world.EffectiveProperty(entity, "value"));
    }

    [Fact]
    public void ATickDoesNotRewriteLiveModifiers()
    {
        var world = new World(seed: 1, valueScale: 1);
        world.LogicalTick = 1;
        var entity = world.Add(new DataEntity([], [], ("value", 0.0)));
        world.SetModifier("source", entity, "value", ModifierOperation.Add, 1, remainingTicks: 50);

        var before = world.Modifiers[0];
        world.AdvanceModifiers();

        Assert.Equal(before, world.Modifiers[0]);
    }

    [Fact]
    public void AdvancingLiveModifiersDoesNotAllocate()
    {
        var world = new World(seed: 1, valueScale: 1);
        world.LogicalTick = 1;
        for (var index = 0; index < 500; index++)
        {
            var entity = world.Add(new DataEntity([], [], ("value", 0.0)));
            world.SetModifier("source", entity, "value", ModifierOperation.Add, 1, remainingTicks: 1_000);
        }

        world.AdvanceModifiers();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 0; tick < 50; tick++)
        {
            world.LogicalTick++;
            world.AdvanceModifiers();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    [Fact]
    public void ACloneBetweenTicksStartsNewModifiersAtTheNextTick()
    {
        var world = new World(seed: 2, valueScale: 1);
        world.LogicalTick = 5;
        world.AdvanceModifiers();

        var clone = world.Clone();
        var entity = clone.Add(new DataEntity([], [], ("value", 0.0)));
        clone.SetModifier("source", entity, "value", ModifierOperation.Add, 1, remainingTicks: 1);

        Assert.Equal(6, Assert.Single(clone.Modifiers).ExpiryTick);
    }

    [Fact]
    public void ARolledBackTickRestoresTheBetweenTicksPhase()
    {
        var world = new World(seed: 3, valueScale: 1);
        world.LogicalTick = 5;
        world.AdvanceModifiers();
        var entity = world.Add(new DataEntity([], [], ("value", 0.0)));

        using (var transaction = world.BeginTransaction())
        {
            world.LogicalTick = 6;
            transaction.Rollback();
        }

        world.SetModifier("source", entity, "value", ModifierOperation.Add, 1, remainingTicks: 1);

        Assert.Equal(6, Assert.Single(world.Modifiers).ExpiryTick);
    }

    [Fact]
    public void TimedGrantStopsMatchingWhenItExpires()
    {
        var world = new World(seed: 1, valueScale: 1);
        world.LogicalTick = 1;
        var entity = world.Add(new DataEntity([], []));

        world.Grant("source", entity, GrantKind.Tag, "blessed", remainingTicks: 2);

        Assert.True(world.HasTag(entity, "blessed"));
        world.AdvanceModifiers();
        Assert.True(world.HasTag(entity, "blessed"));

        world.LogicalTick = 2;
        world.AdvanceModifiers();
        Assert.False(world.HasTag(entity, "blessed"));
    }
}
