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

    [Fact]
    public void Rollback_restores_population_state_after_any_row_sequence()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[]>(RestoresPopulationState));
    }

    [Fact]
    public void Commit_keeps_population_state_for_a_committed_tick()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[]>(KeepsCommittedPopulationState));
    }

    private static bool RestoresPopulationState(int[] values)
    {
        var world = CreatePopulationWorld(out var population);
        var before = DescribePopulation(world, population);

        using (var transaction = world.BeginTransaction())
        {
            foreach (var value in values)
            {
                ApplyPopulation(world, population, value);
            }

            transaction.Rollback();
        }

        return before == DescribePopulation(world, population);
    }

    private static bool KeepsCommittedPopulationState(int[] values)
    {
        var world = CreatePopulationWorld(out var population);
        var expected = CreatePopulationWorld(out var expectedPopulation);

        using (var transaction = world.BeginTransaction())
        {
            foreach (var value in values)
            {
                ApplyPopulation(world, population, value);
                ApplyPopulation(expected, expectedPopulation, value);
            }

            transaction.Commit();
        }

        return DescribePopulation(world, population) == DescribePopulation(expected, expectedPopulation);
    }

    private static World CreatePopulationWorld(out Population<RollbackCrowdKind> population)
    {
        var world = new World(seed: 11, valueScale: 1);
        population = world.DefinePopulation<RollbackCrowdKind>(enableHandles: true);
        var health = population.Column(s_crowdHealthKey);
        population.Reserve(8);
        population.AddRange(2);
        population.WithAccess(new PopulationRange(0, 2), health,
            static (PopulationColumn<int> column, scoped ref PopulationAccessScope<RollbackCrowdKind> scope) =>
            {
                var values = scope.EditRange(column);
                values[0] = 100;
                values[1] = 50;
            });
        return world;
    }

    // One random command per value: value edits, sparse writes, tag bits, modifier slots, and the
    // structural edits (grow, remove-swap-back) that move rows under the journal.
    private static void ApplyPopulation(World world, Population<RollbackCrowdKind> population, int value)
    {
        var amount = Math.Abs(value % 9);
        var row = population.Count == 0 ? 0 : Math.Abs(value % population.Count);
        var health = population.Column(s_crowdHealthKey);
        switch ((value % 13 + 13) % 13)
        {
            case 0:
                if (population.Count > 0)
                {
                    population.WithAccess(new PopulationRange(row, 1), (Column: health, Amount: amount),
                        static ((PopulationColumn<int> Column, int Amount) state,
                            scoped ref PopulationAccessScope<RollbackCrowdKind> scope) =>
                            scope.EditRange(state.Column)[0] = state.Amount);
                }

                break;
            case 1:
                if (population.Count > 0)
                {
                    population.AddTag(row, "poisoned");
                }

                break;
            case 2:
                if (population.Count > 0)
                {
                    population.RemoveTag(row, "poisoned");
                }

                break;
            case 3:
                if (population.Count > 0)
                {
                    population.SetModifier(row, "test.crowd.health", amount, remainingTicks: value % 4);
                }

                break;
            case 4:
                population.AddRange(1);
                break;
            case 5:
                if (population.Count > 1)
                {
                    population.RemoveSwapBack(row);
                }

                break;
            case 6:
                if (population.Count > 0)
                {
                    population.AddTag(row, "blessed");
                }

                break;
            default:
                if (population.Count > 0)
                {
                    _ = population.EffectiveValue(row, "test.crowd.health");
                }

                break;
        }
    }

    private static string DescribePopulation(World world, Population<RollbackCrowdKind> population)
    {
        var text = new StringBuilder();
        text.Append("count:").Append(population.Count).Append(";");
        for (var row = 0; row < population.Count; row++)
        {
            text.Append("row:").Append(row)
                .Append(',').Append(population.EffectiveValue(row, "test.crowd.health"))
                .Append(',').Append(population.HasTag(row, "poisoned"))
                .Append(',').Append(population.HasTag(row, "blessed"))
                .Append(';');
        }

        return text.ToString();
    }

    private static readonly PopulationColumnKey<int> s_crowdHealthKey =
        new("test.crowd.health", 1, PopulationCodecs.Int32);

    private readonly struct RollbackCrowdKind : IPopulationKind
    {
        public static string Id => "test.rollback-crowd";

        public static int Version => 1;

        public static string[] Tags => ["poisoned", "blessed"];

        public static string[] ModifierProperties => ["test.crowd.health"];
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
