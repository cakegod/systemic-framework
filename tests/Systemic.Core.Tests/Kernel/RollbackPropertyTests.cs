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
    public void RollbackRestoresWorldStateAfterAnyCommandSequence()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[]>(RestoresState));
    }

    [Fact]
    public void CommitKeepsChangesForACommittedTick()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[]>(KeepsCommittedState));
    }

    [Fact]
    public void RollbackRestoresPopulationStateAfterAnyRowSequence()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[]>(RestoresPopulationState));
    }

    [Fact]
    public void CommitKeepsPopulationStateForACommittedTick()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[]>(KeepsCommittedPopulationState));
    }

    // The narrow alphabet writes one row at a time, so it never produces the row capture, range
    // capture, row capture interleaving that reordered the undo journal. These sequences mix range
    // writes with structural edits and compare every derived lookup, not only column values.
    [Fact]
    public void RollbackRestoresDerivedPopulationStateAfterAnyMixedSequence()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[], int[]>((first, second) =>
            RestoresDerivedPopulationState([.. first, .. second])));
    }

    [Fact]
    public void CommitKeepsDerivedPopulationStateForACommittedTick()
    {
        Check.One(Config.QuickThrowOnFailure, Prop.ForAll<int[], int[]>((first, second) =>
            KeepsCommittedDerivedPopulationState([.. first, .. second])));
    }

    private static bool RestoresDerivedPopulationState(int[] values)
    {
        var world = CreatePopulationWorld(out var population);
        var before = DescribeDerivedPopulation(world, population);

        using (var transaction = world.BeginTransaction())
        {
            foreach (var value in values)
            {
                ApplyMixedPopulation(world, population, value);
            }

            transaction.Rollback();
        }

        return before == DescribeDerivedPopulation(world, population);
    }

    private static bool KeepsCommittedDerivedPopulationState(int[] values)
    {
        var world = CreatePopulationWorld(out var population);
        var expected = CreatePopulationWorld(out var expectedPopulation);

        using (var transaction = world.BeginTransaction())
        {
            foreach (var value in values)
            {
                ApplyMixedPopulation(world, population, value);
                ApplyMixedPopulation(expected, expectedPopulation, value);
            }

            transaction.Commit();
        }

        return DescribeDerivedPopulation(world, population)
            == DescribeDerivedPopulation(expected, expectedPopulation);
    }

    // Range writes over several rows interleaved with additions and swap-back removals, plus the
    // modifier and tag writes whose derived lookup caches must follow the same journal.
    private static void ApplyMixedPopulation(World world, Population<RollbackCrowdKind> population, int value)
    {
        var amount = Math.Abs(value % 9);
        var row = population.Count == 0 ? 0 : Math.Abs(value % population.Count);
        var health = population.Column(s_crowdHealthKey);
        switch ((value % 16 + 16) % 16)
        {
            case 0:
                // Row-capturing removal, range capture, row-capturing removal: the sandwich that
                // replayed a row batch after the page captured between its captures.
                if (population.Count >= 3)
                {
                    population.RemoveSwapBack(0);
                    population.WithAccess(new PopulationRange(0, population.Count),
                        (Column: health, Amount: amount),
                        static ((PopulationColumn<int> Column, int Amount) state,
                            scoped ref PopulationAccessScope<RollbackCrowdKind> scope) =>
                            scope.EditRange(state.Column).Fill(state.Amount));
                    population.RemoveSwapBack(Math.Min(1, population.Count - 1));
                }

                break;
            case 1:
            case 2:
                if (population.Count > 0)
                {
                    population.WithAccess(new PopulationRange(row, 1), (Column: health, Amount: amount),
                        static ((PopulationColumn<int> Column, int Amount) state,
                            scoped ref PopulationAccessScope<RollbackCrowdKind> scope) =>
                            scope.EditRange(state.Column)[0] = state.Amount);
                }

                break;
            case 3:
            case 4:
                if (population.Count > 0)
                {
                    var start = Math.Min(row, population.Count - 1);
                    var length = Math.Min(1 + Math.Abs(value % 3), population.Count - start);
                    population.WithAccess(new PopulationRange(start, length), (Column: health, Amount: amount),
                        static ((PopulationColumn<int> Column, int Amount) state,
                            scoped ref PopulationAccessScope<RollbackCrowdKind> scope) =>
                            scope.EditRange(state.Column).Fill(state.Amount));
                }

                break;
            case 5:
                if (population.Count > 0)
                {
                    population.AddTag(row, "poisoned");
                }

                break;
            case 6:
                if (population.Count > 0)
                {
                    population.RemoveTag(row, "poisoned");
                }

                break;
            case 7:
                if (population.Count > 0)
                {
                    population.SetModifier(row, "test.crowd.health", amount, remainingTicks: value % 4);
                }

                break;
            case 8:
                if (population.Count > 0)
                {
                    population.SetModifier(row, "test.crowd.health", -amount);
                }

                break;
            case 9:
                population.AddRange(1);
                break;
            case 10:
                population.AddRange(2);
                break;
            case 11:
                if (population.Count > 1)
                {
                    population.RemoveSwapBack(row);
                }

                break;
            case 12:
                if (population.Count > 0)
                {
                    population.AddTag(row, "blessed");
                }

                break;
            case 13:
                if (population.Count > 0)
                {
                    population.RemoveTag(row, "blessed");
                }

                break;
            case 14:
                if (population.Count > 0)
                {
                    population.WithAccess(new PopulationRange(0, population.Count), (Column: health, Amount: amount),
                        static ((PopulationColumn<int> Column, int Amount) state,
                            scoped ref PopulationAccessScope<RollbackCrowdKind> scope) =>
                            scope.EditRange(state.Column).Fill(state.Amount));
                }

                break;
            default:
                if (population.Count > 0)
                {
                    _ = population.EffectiveValue(row, "test.crowd.health");
                    _ = population.HasTag(row, "poisoned");
                }

                break;
        }
    }

    private static string DescribeDerivedPopulation(World world, Population<RollbackCrowdKind> population)
    {
        var text = new StringBuilder(DescribePopulation(world, population));
        for (var row = 0; row < population.Count; row++)
        {
            var handle = population.GetHandle(row);
            text.Append("handle:").Append(handle.Slot).Append(',').Append(handle.Generation)
                .Append(",resolves:").Append(population.TryResolve(handle, out var resolved) && resolved == row)
                .Append(';');
        }

        return text.ToString();
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
        population.AddRange(4);
        population.WithAccess(new PopulationRange(0, 4), health,
            static (PopulationColumn<int> column, scoped ref PopulationAccessScope<RollbackCrowdKind> scope) =>
            {
                var values = scope.EditRange(column);
                values[0] = 100;
                values[1] = 50;
                values[2] = 75;
                values[3] = 25;
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
        text.Append("count:").Append(population.Count).Append(';');
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

        return before == Describe(world, entity) && DerivedLookupsAreConsistent(world);
    }

    private static bool KeepsCommittedState(int[] values)
    {
        var world = new World(seed: 5, valueScale: 1);
        var entity = world.Add(new DataEntity(["t"], [], ("a", 0.0), ("b", 1.0)));
        var expected = new World(seed: 5, valueScale: 1);
        var expectedEntity = expected.Add(new DataEntity(["t"], [], ("a", 0.0), ("b", 1.0)));
        // Warm the derived caches before the mutations, so a missing invalidation leaves a stale entry.
        _ = Describe(world, entity);
        _ = Describe(expected, expectedEntity);

        using (var transaction = world.BeginTransaction())
        {
            foreach (var value in values)
            {
                Apply(world, entity, value);
                Apply(expected, expectedEntity, value);
            }

            transaction.Commit();
        }

        return Describe(world, entity) == Describe(expected, expectedEntity)
            && DerivedLookupsAreConsistent(world);
    }

    // The cached lookups must agree with a scan of the entities. Comparing two observations of the
    // same world cannot catch a stale cache: a rollback restores exactly the state the cache holds.
    private static bool DerivedLookupsAreConsistent(World world)
    {
        var live = 0;
        var first = 0;
        foreach (var entity in world.Entities)
        {
            if (entity.ToRemove || !world.HasTag(entity, "t"))
            {
                continue;
            }

            if (live == 0)
            {
                first = entity.Id;
            }

            live++;
        }

        return world.LiveCount("t") == live && world.FirstTagged("t", "") == first;
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
                if (amount > 3)
                {
                    world.Remove(entity);
                }

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
        text.Append("|grantedCount:").Append(world.EntitiesWithTag("granted").Length);
        text.Append("|live:").Append(world.LiveCount("t")).Append(',').Append(world.LiveRank("t", entity.Id));
        text.Append("|first:").Append(world.FirstTagged("t", "")).Append(',')
            .Append(world.FirstTagged("granted", ""));

        return text.ToString();
    }
}
