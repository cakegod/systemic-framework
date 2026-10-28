using System.Text;
using FsCheck;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

/// <summary>
/// Random command sequences against the World journal. Examples cannot cover the combinations of
/// structural edits, modifiers, grants and bounds that a single rolled-back tick can produce.
/// </summary>
public sealed class RollbackPropertyTests
{
    [Fact]
    public void Rollback_restores_world_state_after_any_command_sequence()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[]>(RestoresState));
    }

    [Fact]
    public void Commit_keeps_changes_for_a_committed_tick()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[]>(KeepsCommittedState));
    }

    private static bool RestoresState(int[] values)
    {
        var world = new World(seed: 5, valueScale: 1);
        var entity = world.Add(new DataEntity(["t"], [], ("a", 0.0), ("b", 1.0)));
        var before = Describe(world, entity);

        using (var transaction = world.BeginTransaction())
        {
            foreach (var value in values)
            {
                Apply(world, entity, value);
            }

            transaction.Rollback();
        }

        return before == Describe(world, entity);
    }

    private static bool KeepsCommittedState(int[] values)
    {
        var world = new World(seed: 5, valueScale: 1);
        var entity = world.Add(new DataEntity(["t"], [], ("a", 0.0), ("b", 1.0)));
        var expected = new World(seed: 5, valueScale: 1);
        var expectedEntity = expected.Add(new DataEntity(["t"], [], ("a", 0.0), ("b", 1.0)));

        using (var transaction = world.BeginTransaction())
        {
            foreach (var value in values)
            {
                Apply(world, entity, value);
                Apply(expected, expectedEntity, value);
            }

            transaction.Commit();
        }

        return Describe(world, entity) == Describe(expected, expectedEntity);
    }

    private static void Apply(World world, Entity entity, int value)
    {
        var amount = value % 7;
        var ticks = value % 5;
        switch ((value % 11 + 11) % 11)
        {
            case 0:
                _ = entity.TrySetProperty(world, "a", amount);
                break;
            case 1:
                entity.AddTag("t");
                break;
            case 2:
                entity.RemoveTag("t");
                break;
            case 3:
                world.SetModifier("prop.source", entity, "a", ModifierOperation.Add, amount, remainingTicks: ticks);
                break;
            case 4:
                world.RemoveModifier("prop.source", entity, "a");
                break;
            case 5:
                world.Grant("prop.source", entity, GrantKind.Tag, "granted", remainingTicks: ticks);
                break;
            case 6:
                world.Revoke("prop.source", entity, GrantKind.Tag, "granted");
                break;
            case 7:
                world.SetSpatialBounds(entity, new Bounds2(amount, amount, 1, 1));
                break;
            case 8:
                _ = world.RemoveSpatialBounds(entity);
                break;
            case 9:
                _ = world.Add(new DataEntity(["t"], [], ("c", amount)));
                break;
            default:
                entity.ToRemove = amount > 3;
                break;
        }
    }

    private static string Describe(World world, Entity entity)
    {
        var text = new StringBuilder();
        foreach (var modifier in world.Modifiers)
        {
            text.Append(modifier.Source).Append(',').Append(modifier.EntityId).Append(',')
                .Append(modifier.Property).Append(',').Append(modifier.Operation).Append(',')
                .Append(modifier.Value).Append(',').Append(modifier.ExpiryTick).Append(',')
                .Append(modifier.Priority).Append(',').Append(modifier.Stage).Append(';');
        }

        foreach (var grant in world.Grants)
        {
            text.Append(grant.Source).Append(',').Append(grant.EntityId).Append(',')
                .Append(grant.Kind).Append(',').Append(grant.Name).Append(',')
                .Append(grant.ExpiryTick).Append(';');
        }

        text.Append("value:").Append(entity.TryGetProperty(world, "a", out var a) ? a : double.NaN);
        text.Append("|effective:").Append(world.EffectiveProperty(entity, "a"));
        text.Append("|tagged:").Append(entity.HasTag("t")).Append("|granted:")
            .Append(entity.HasTag("granted"));
        text.Append("|bounds:").Append(world.TryGetSpatialBounds(entity.Id, out var bounds)
            ? bounds.ToString()
            : "none");
        text.Append("|entities:").Append(world.Entities.Count).Append("|nextId:").Append(world.NextEntityId);
        text.Append("|remove:").Append(entity.ToRemove).Append("|taggedCount:")
            .Append(world.EntitiesWithTag("t").Length);

        return text.ToString();
    }
}
