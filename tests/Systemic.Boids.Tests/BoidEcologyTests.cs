using System.Numerics;
using System.Collections.Immutable;
using System.Text.Json;
using Boids.MonoGame;
using Systemic.Kernel;
using Systemic.Content;
using Systemic.Host;
using Systemic.Rules;
using Systemic.Save;

namespace Systemic.Tests.Boids;

public sealed class BoidEcologyTests
{
    private const int TickRateHz = 30;

    [Fact]
    public void DeterministicInitializationMatchesSeed()
    {
        var setup = new Setup { BoidCount = 32, Seed = 41 };
        var (first, second) = (CreateFlock(setup), CreateFlock(setup));

        Assert.Equal(first.Flock.Positions.ToArray(), second.Flock.Positions.ToArray());
        Assert.Equal(first.Flock.Velocities.ToArray(), second.Flock.Velocities.ToArray());
        Assert.Equal(first.Flock.Energies.ToArray(), second.Flock.Energies.ToArray());
        Assert.Equal(first.Flock.SpeciesByBoid.ToArray(), second.Flock.SpeciesByBoid.ToArray());
        AssertKernelPopulationMatchesFlock(first.World, first.Flock);
        AssertKernelPopulationMatchesFlock(second.World, second.Flock);
    }

    [Fact]
    public void PopulationStateChangesThroughKernelOwnedColumns()
    {
        var setup = new Setup { BoidCount = 64, Seed = 29 };
        var (world, flock) = CreateFlock(setup);
        var before = GetBoidPopulation(world);

        Step(flock, world, 1);

        var after = GetBoidPopulation(world);
        var positionsBefore = GetColumn<Vector2>(before, "position", PopulationCodecs.Vector2);
        var positionsAfter = GetColumn<Vector2>(after, "position", PopulationCodecs.Vector2);
        Assert.False(positionsBefore.SequenceEqual(positionsAfter));
        AssertKernelPopulationMatchesFlock(world, flock);
    }

    [Fact]
    public void WarmedTickDoesNotAllocateOnSimulationThread()
    {
        const int warmupTicks = 200;
        const int measuredTicks = 20;
        var setup = new Setup { BoidCount = 512, Seed = 73 };
        var (world, flock) = CreateFlock(setup);
        using var disposeFlock = flock;
        var rules = new RuleEngine(new CompiledRuleSet([]));
        var contexts = Enumerable.Range(0, warmupTicks + measuredTicks)
            .Select(tick => new SystemicSystemContext(world, rules, tick))
            .ToArray();
        for (var tick = 0; tick < warmupTicks; tick++)
        {
            flock.Step(contexts[tick]);
        }

        var currentThreadAllocations = new long[measuredTicks];
        for (var tick = warmupTicks; tick < contexts.Length; tick++)
        {
            var currentThreadBefore = GC.GetAllocatedBytesForCurrentThread();
            flock.Step(contexts[tick]);
            var index = tick - warmupTicks;
            currentThreadAllocations[index] = GC.GetAllocatedBytesForCurrentThread() - currentThreadBefore;
        }

        Assert.True(currentThreadAllocations.All(value => value == 0),
            $"Current-thread allocation per tick: {string.Join(",", currentThreadAllocations)} bytes.");
    }

    [Fact]
    public void ParallelPartitionsPreserveSeededStateDeterministically()
    {
        var setup = new Setup { BoidCount = 512, Seed = 89 };
        var (first, second) = (CreateFlock(setup), CreateFlock(setup));
        using var disposeFirst = first.Flock;
        using var disposeSecond = second.Flock;

        Step(first.Flock, first.World, 8);
        Step(second.Flock, second.World, 8);

        Assert.Equal(first.Flock.Positions.ToArray(), second.Flock.Positions.ToArray());
        Assert.Equal(first.Flock.Velocities.ToArray(), second.Flock.Velocities.ToArray());
        Assert.Equal(first.Flock.Energies.ToArray(), second.Flock.Energies.ToArray());
        Assert.Equal(first.Flock.SpeciesByBoid.ToArray(), second.Flock.SpeciesByBoid.ToArray());
    }

    [Fact]
    public void WarmedSingleWorkerScheduledBoidTicksDoNotAllocateOnSimulationThread()
    {
        const int warmupTicks = 200;
        const int measuredTicks = 20;
        var setup = new Setup { BoidCount = 512, Seed = 79 };
        var (world, flock) = CreateFlock(setup);
        using var disposeFlock = flock;
        using var game = new SystemicGameBuilder(CreateTestContent(), world)
            .AddService(flock)
            .AddSystem(flock)
            .WithScheduledWorkerCount(1)
            .Build();

        for (var tick = 0; tick < warmupTicks; tick++)
        {
            game.Step();
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var tick = 0; tick < measuredTicks; tick++)
        {
            game.Step();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
    }

    [Fact]
    public void FailedLaterScheduledSystemRestoresThePredatorsFoodTarget()
    {
        var setup = new Setup { BoidCount = 64, Seed = 83, AddFood = true };
        var (world, flock) = CreateFlock(setup);
        using var disposeFlock = flock;
        using var game = new SystemicGameBuilder(CreateTestContent(), world)
            .AddService(flock)
            .AddSystem(flock)
            .AddSystem(new ThrowingScheduledSystem())
            .WithScheduledWorkerCount(4)
            .Build();

        Assert.Throws<InvalidOperationException>(game.Step);

        Assert.Equal(JsonValueKind.Null,
            flock.CaptureState().GetProperty("predatorTargetFoodEntityId"u8).ValueKind);
        Assert.Equal(0, world.LogicalTick);
    }

    [Fact]
    public void MultipleTicksInsideAnOuterTransactionRestorePublicViewsAndPopulationOnRollback()
    {
        var (world, flock) = CreateFlock(new Setup { BoidCount = 64, Seed = 83, AddFood = true });
        using var disposeFlock = flock;
        using var game = new SystemicGameBuilder(CreateTestContent(), world)
            .AddService(flock).AddSystem(flock).WithScheduledWorkerCount(1).Build();
        var positions = flock.Positions.ToArray();
        var food = flock.FoodPatches.ToArray();
        var predator = flock.PredatorState;
        var hungry = flock.HungryFraction;
        using var outer = world.BeginTransaction();
        game.Step();
        game.Step();

        outer.Rollback();

        Assert.Equal(0, world.LogicalTick);
        Assert.Equal(positions, flock.Positions.ToArray());
        Assert.Equal(food, flock.FoodPatches.ToArray());
        Assert.Equal(predator, flock.PredatorState);
        Assert.Equal(hungry, flock.HungryFraction);
    }

    [Fact]
    public void KernelSchedulePreservesSeededEcologyAcrossWorkerCounts()
    {
        var setup = new Setup { BoidCount = 512, Seed = 127, AddFood = true };
        var (first, second) = (CreateFlock(setup), CreateFlock(setup));
        using var disposeFirst = first.Flock;
        using var disposeSecond = second.Flock;
        using var firstGame = new SystemicGameBuilder(CreateTestContent(), first.World)
            .AddService(first.Flock)
            .AddSystem(first.Flock)
            .WithScheduledWorkerCount(1)
            .Build();
        using var secondGame = new SystemicGameBuilder(CreateTestContent(), second.World)
            .AddService(second.Flock)
            .AddSystem(second.Flock)
            .WithScheduledWorkerCount(4)
            .Build();

        for (var tick = 0; tick < 8; tick++)
        {
            firstGame.Step();
            secondGame.Step();
        }

        Assert.Equal(first.Flock.Positions.ToArray(), second.Flock.Positions.ToArray());
        Assert.Equal(first.Flock.Velocities.ToArray(), second.Flock.Velocities.ToArray());
        Assert.Equal(first.Flock.Energies.ToArray(), second.Flock.Energies.ToArray());
        Assert.Equal(first.Flock.SpeciesByBoid.ToArray(), second.Flock.SpeciesByBoid.ToArray());
        Assert.Equal(first.Flock.FoodPatches.ToArray(), second.Flock.FoodPatches.ToArray());
        Assert.Equal(first.Flock.PredatorState, second.Flock.PredatorState);
    }

    [Fact]
    public static void WorkerResourcesCanBeDisposedBeforeTheFirstStep()
    {
        var setup = new Setup { BoidCount = 512, Seed = 97 };
        var (_, flock) = CreateFlock(setup);

        flock.Dispose();
        flock.Dispose();
    }

    [Fact]
    public void SaveLoadContinuationMatchesUninterruptedSimulation()
    {
        var setup = new Setup { BoidCount = 48, Seed = 41, AddFood = true };
        var content = CreateTestContent();
        var (world, flock) = CreateFlock(setup);
        var materialized = new MaterializedContentWorld(world,
            ImmutableDictionary<string, int>.Empty.Add("flock.main", FindFlockEntityId(world)));
        var game = new SystemicGameBuilder(content, materialized).AddSystem(flock).Build();
        Step(flock, world, 12);
        var codec = new SystemicSaveCodec();
        var save = codec.Save(game);

        var loaded = codec.Load(content, save);
        var restoredFlock = new BoidFlockSystem(loaded.World, loaded.EntityIds["flock.main"], setup.Seed, TickRateHz);
        _ = new SystemicGameBuilder(content, loaded).AddSystem(restoredFlock).Build();
        AssertKernelPopulationMatchesFlock(loaded.World, restoredFlock);

        Step(flock, world, 8, firstTick: 12);
        Step(restoredFlock, loaded.World, 8, firstTick: 12);

        Assert.Equal(flock.Positions.ToArray(), restoredFlock.Positions.ToArray());
        Assert.Equal(flock.Velocities.ToArray(), restoredFlock.Velocities.ToArray());
        Assert.Equal(flock.Energies.ToArray(), restoredFlock.Energies.ToArray());
        Assert.Equal(flock.SpeciesByBoid.ToArray(), restoredFlock.SpeciesByBoid.ToArray());
        Assert.Equal(flock.PredatorState, restoredFlock.PredatorState);
        Assert.Equal(flock.FoodPatches.ToArray(), restoredFlock.FoodPatches.ToArray());
    }

    [Fact]
    public void SaveLoadRestoresPredatorTargetByFoodEntityIdWhenItIsNoLongerRichest()
    {
        var setup = new Setup
        {
            BoidCount = 32,
            Seed = 211,
            AddFood = true,
            FoodStock = 40.0f,
            FoodCapacity = 1000.0f,
            FoodFeedAmount = 0.01f,
            FoodFeedRadius = 1.0f
        };
        var world = CreateWorld(setup, out var flockId);
        world.Add(new DataEntity(["food.patch"], [],
            ("food.positionX", setup.WorldSize * 0.5f),
            ("food.positionY", setup.WorldSize * 0.5f),
            ("food.capacity", setup.FoodCapacity),
            ("food.stock", 35.0),
            ("food.regenPerSecond", 0.0),
            ("food.feedAmount", setup.FoodFeedAmount),
            ("food.feedRadius", setup.FoodFeedRadius)));
        var foodEntityIds = world.Entities.Where(entity => world.HasTag(entity, "food.patch"))
            .OrderBy(entity => entity.Id)
            .Select(entity => entity.Id)
            .ToArray();
        var flock = new BoidFlockSystem(world, flockId, setup.Seed, TickRateHz);
        using var disposeFlock = flock;
        var predator = Assert.Single(world.Entities.Where(entity => world.HasTag(entity, "boids.predator")));
        var initialTarget = flock.FoodPatches[0];
        Assert.True(predator.TrySetProperty(world, "predator.positionX", initialTarget.Position.X));
        Assert.True(predator.TrySetProperty(world, "predator.positionY", initialTarget.Position.Y));

        var content = CreateTestContent();
        var materialized = new MaterializedContentWorld(world,
            ImmutableDictionary<string, int>.Empty.Add("flock.main", flockId));
        var game = new SystemicGameBuilder(content, materialized).AddSystem(flock).Build();
        game.Step();

        Assert.True(flock.FoodPatches[0].Stock > 0.0f);
        Assert.True(flock.FoodPatches[0].Stock < flock.FoodPatches[1].Stock,
            "The predator's chosen patch must remain stocked but stop being the richest patch before saving.");
        var codec = new SystemicSaveCodec();
        var save = codec.Save(game);
        game.Step();
        var expectedFood = flock.FoodPatches.ToArray();
        var expectedPredator = flock.PredatorState;

        var loaded = codec.Load(content, save);
        var restoredFlock = new BoidFlockSystem(loaded.World, loaded.EntityIds["flock.main"], setup.Seed, TickRateHz);
        using var disposeRestoredFlock = restoredFlock;
        var restoredGame = new SystemicGameBuilder(content, loaded).AddSystem(restoredFlock).Build();
        restoredGame.Step();

        Assert.Equal(expectedFood, restoredFlock.FoodPatches.ToArray());
        Assert.Equal(expectedPredator, restoredFlock.PredatorState);
        using var savedDocument = JsonDocument.Parse(save);
        var savedTargetEntityId = savedDocument.RootElement.GetProperty("extensionStates"u8)
            .GetProperty("sample.boids.flock"u8)
            .GetProperty("predatorTargetFoodEntityId"u8)
            .GetInt32();
        Assert.Equal(foodEntityIds[0], savedTargetEntityId);
    }

    [Fact]
    public void SavedPredatorTargetMustResolveToAKnownFoodPatch()
    {
        var setup = new Setup { BoidCount = 32, Seed = 223, AddFood = true };
        var (world, flock) = CreateFlock(setup);
        using var disposeFlock = flock;
        using var invalidState = JsonDocument.Parse("""
            {"version":1,"predatorTargetFoodEntityId":2147483647}
            """);

        Assert.Throws<InvalidDataException>(() => flock.RestoreState(invalidState.RootElement));
    }

    [Fact]
    public void InvalidSavedStateFailsDuringBuildWithoutAdvancingThePopulation()
    {
        var setup = new Setup { BoidCount = 512, Seed = 227, AddFood = true };
        var world = CreateWorld(setup, out var flockId);
        var flock = new BoidFlockSystem(world, flockId, setup.Seed, TickRateHz);
        using var disposeFlock = flock;
        var positionsBeforeBuild = flock.Positions.ToArray();
        using var invalidState = JsonDocument.Parse("""
            {"version":1,"predatorTargetFoodEntityId":2147483647}
            """);
        var extensionStates = ImmutableDictionary<string, JsonElement>.Empty
            .Add(flock.Id, invalidState.RootElement.Clone());
        var builder = new SystemicGameBuilder(CreateTestContent(), world, extensionStates: extensionStates)
            .AddSystem(flock);

        Assert.Throws<InvalidDataException>(() => builder.Build());
        Assert.Equal(positionsBeforeBuild, flock.Positions.ToArray());
    }

    [Fact]
    public void BoidsWantFoodOnlyBelowTheHungerThreshold()
    {
        Assert.True(BoidFlockSystem.WantsFood(0.2f));
        Assert.True(BoidFlockSystem.WantsFood(0.649f));
        Assert.False(BoidFlockSystem.WantsFood(0.65f));
        Assert.False(BoidFlockSystem.WantsFood(1.0f));
    }

    [Fact]
    public void EnergyScalesSpeedBetweenTheStarvedFactorAndFullSpeed()
    {
        Assert.Equal(0.4f, BoidFlockSystem.EnergySpeedFactor(0.0f, 0.4f), 3);
        Assert.Equal(0.7f, BoidFlockSystem.EnergySpeedFactor(0.5f, 0.4f), 3);
        Assert.Equal(1.0f, BoidFlockSystem.EnergySpeedFactor(1.0f, 0.4f), 3);
        Assert.Equal(1.0f, BoidFlockSystem.EnergySpeedFactor(2.0f, 0.4f), 3);
    }

    [Fact]
    public void HungryBoidsSpendPatchStockAndRestoreEnergy()
    {
        var setup = new Setup
        {
            BoidCount = 128,
            WorldSize = 120.0f,
            AddFood = true,
            FoodStock = 5000.0f,
            FoodCapacity = 5000.0f,
            FoodFeedRadius = 60.0f,
            FoodFeedAmount = 0.05f,
            PredatorBoidEnergyDrainPerSecond = 0.0f
        };
        var world = CreateWorld(setup, out var flockId);
        var flock = new BoidFlockSystem(world, flockId, setup.Seed, TickRateHz);
        var energyBefore = flock.Energies.ToArray();
        var stockBefore = flock.FoodPatches[0].Stock;

        Step(flock, world, 40);

        var restored = 0;
        for (var boid = 0; boid < energyBefore.Length; boid++)
        {
            if (flock.Energies[boid] > energyBefore[boid])
            {
                restored++;
            }
        }

        Assert.True(restored > 0, "No hungry boid restored energy from the stocked patch.");
        Assert.True(flock.FoodPatches[0].Stock < stockBefore, "Boid feeding did not spend patch stock.");
        Assert.InRange(flock.HungryFraction, 0.0f, 1.0f);
    }

    [Fact]
    public void PredatorFillsUpAtFoodAndStopsHunting()
    {
        var setup = new Setup
        {
            PredatorEnergy = 0.2f,
            PredatorEnergyDrainPerSecond = 0.0f,
            PredatorEnergyPerStock = 0.02f,
            PredatorSatedThreshold = 0.85f,
            PredatorRestSpeedFactor = 0.3f,
            AddFood = true,
            FoodStock = 5000.0f,
            FoodCapacity = 5000.0f,
            FoodFeedRadius = 5.0f
        };
        var world = CreateWorld(setup, out var flockId);
        var flock = new BoidFlockSystem(world, flockId, setup.Seed, TickRateHz);

        Step(flock, world, 150);

        Assert.True(flock.PredatorState.Energy > 0.5f, $"Predator energy stayed at {flock.PredatorState.Energy}.");
        Assert.True(flock.PredatorState.Sated, "A fed predator must be sated.");
        Assert.True(flock.PredatorState.FleeRadius < setup.PredatorFleeRadius * 0.7f,
            $"A sated predator must rest inside a smaller radius, got {flock.PredatorState.FleeRadius}.");
    }

    [Fact]
    public void PredatorWakesWhenItsEnergyDrains()
    {
        var setup = new Setup
        {
            PredatorEnergy = 0.95f,
            PredatorEnergyDrainPerSecond = 1.0f,
            PredatorSatedThreshold = 0.9f,
            PredatorRestSpeedFactor = 0.3f,
            AddFood = false
        };
        var world = CreateWorld(setup, out var flockId);
        var flock = new BoidFlockSystem(world, flockId, setup.Seed, TickRateHz);
        var restingRadius = flock.PredatorState.FleeRadius;

        Assert.True(flock.PredatorState.Sated);
        Step(flock, world, 15);

        Assert.False(flock.PredatorState.Sated);
        Assert.True(flock.PredatorState.FleeRadius > restingRadius + 5.0f,
            $"A hungry predator must widen its threat radius from {restingRadius}, got {flock.PredatorState.FleeRadius}.");
    }

    [Fact]
    public void PredatorDrainsEnergyFromBoidsInsideItsConsumeRadius()
    {
        static float MeanEnergy(Setup setup)
        {
            var world = CreateWorld(setup, out var flockId);
            var flock = new BoidFlockSystem(world, flockId, setup.Seed, TickRateHz);
            Step(flock, world, 30);
            var total = 0.0;
            foreach (var energy in flock.Energies)
            {
                total += energy;
            }

            return (float)(total / flock.Energies.Length);
        }

        var calm = new Setup
        {
            BoidCount = 128,
            PredatorEnergy = 0.5f,
            PredatorSatedThreshold = 0.9f,
            PredatorFleeRadius = 60.0f,
            PredatorConsumeRadius = 400.0f,
            PredatorBoidEnergyDrainPerSecond = 0.0f
        };
        var hunting = calm with { PredatorBoidEnergyDrainPerSecond = 3.0f };

        var calmEnergy = MeanEnergy(calm);
        var huntedEnergy = MeanEnergy(hunting);

        Assert.True(huntedEnergy < calmEnergy - 0.1f,
            $"Boids under a hunting predator kept mean energy {huntedEnergy} against {calmEnergy} without predation.");
    }

    [Fact]
    public void SpeciesFleeWeightScattersBoidsFromThePredator()
    {
        static double MeanPredatorDistance(Setup setup)
        {
            var world = CreateWorld(setup, out var flockId);
            var flock = new BoidFlockSystem(world, flockId, setup.Seed, TickRateHz);
            Step(flock, world, 90);

            var total = 0.0;
            foreach (var position in flock.Positions)
            {
                var dx = MathF.Abs(position.X - setup.WorldSize * 0.5f);
                var dy = MathF.Abs(position.Y - setup.WorldSize * 0.5f);
                dx = MathF.Min(dx, setup.WorldSize - dx);
                dy = MathF.Min(dy, setup.WorldSize - dy);
                total += Math.Sqrt(dx * dx + dy * dy);
            }

            return total / flock.Population;
        }

        var indifferent = new Setup
        {
            BoidCount = 192,
            WorldSize = 200.0f,
            SpeciesFleeWeights = [0.0f, 0.0f],
            PredatorEnergy = 0.5f,
            PredatorSatedThreshold = 0.9f,
            PredatorFleeRadius = 60.0f,
            PredatorConsumeRadius = 20.0f
        };
        var skittish = indifferent with { SpeciesFleeWeights = [0.0f, 4.0f] };

        var indifferentDistance = MeanPredatorDistance(indifferent);
        var skittishDistance = MeanPredatorDistance(skittish);

        Assert.True(skittishDistance > indifferentDistance + 1.0,
            $"Fleeing species stayed at mean distance {skittishDistance:F2} against {indifferentDistance:F2} when indifferent.");
    }

    private static void Step(BoidFlockSystem flock, World world, int ticks, int firstTick = 0)
    {
        var rules = new RuleEngine(new CompiledRuleSet([]));
        for (var tick = firstTick; tick < firstTick + ticks; tick++)
        {
            flock.Step(new SystemicSystemContext(world, rules, tick));
        }
    }

    private static (World World, BoidFlockSystem Flock) CreateFlock(Setup setup)
    {
        var world = CreateWorld(setup, out var flockId);
        var flock = new BoidFlockSystem(world, flockId, setup.Seed, TickRateHz);
        return (world, flock);
    }

    private static ContentPack CreateTestContent() => new(
        "test-content",
        new GameManifest("test.boids", "Test Boids", TickRateHz, 5),
        new CompiledRuleSet([]),
        new CompiledEntities([]))
    {
        ContentFingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes("test.boids-synthetic-content-v1")))
    };

    private sealed class ThrowingScheduledSystem : ISystemicSystem
    {
        public string Id => "test.failure";
        public int Order => 200;
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];
        public void Step(SystemicSystemContext gameContext) =>
            throw new InvalidOperationException("expected");
    }

    private static int FindFlockEntityId(World world) =>
        world.Entities.Single(entity => world.HasTag(entity, "boids.flock")).Id;

    private static PopulationSaveState GetBoidPopulation(World world) =>
        Assert.Single(world.CapturePopulationState().Populations);

    private static void AssertKernelPopulationMatchesFlock(World world, BoidFlockSystem flock)
    {
        var population = GetBoidPopulation(world);
        Assert.Equal("sample.boids", population.Id);
        Assert.Equal(flock.Population, population.RowCount);
        Assert.Equal(flock.Positions.ToArray(), GetColumn<Vector2>(population, "position", PopulationCodecs.Vector2));
        Assert.Equal(flock.Velocities.ToArray(), GetColumn<Vector2>(population, "velocity", PopulationCodecs.Vector2));
        Assert.Equal(flock.Energies.ToArray(), GetColumn<float>(population, "energy", PopulationCodecs.Single));
        Assert.Equal(flock.SpeciesByBoid.ToArray(), GetColumn<int>(population, "species", PopulationCodecs.Int32));
    }

    private static T[] GetColumn<T>(PopulationSaveState population, string id, IPopulationCodec<T> codec)
        where T : unmanaged
    {
        var column = Assert.Single(population.Columns.Where(column => column.Id == id));
        var values = new T[population.RowCount];
        codec.Decode(column.Data, values);
        return values;
    }

    private static World CreateWorld(Setup setup, out int flockId)
    {
        var world = new World(seed: setup.Seed, valueScale: 1.0);
        var flock = world.Add(new DataEntity(["boids.flock"], [],
            ("boids.population", setup.BoidCount),
            ("boids.worldWidth", setup.WorldSize),
            ("boids.worldHeight", setup.WorldSize),
            ("boids.neighborRadius", 24.0),
            ("boids.collisionRadius", 4.0),
            ("boids.collisionStrength", 36.0),
            ("boids.friction", 0.35),
            ("boids.particleRadius", 1.1),
            ("boids.maxSpeed", 54.0),
            ("boids.maxForce", 30.0),
            ("boids.starvedSpeedFactor", setup.StarvedSpeedFactor),
            ("boids.energyDrainPerSecond", setup.EnergyDrainPerSecond)));
        flockId = flock.Id;

        for (var species = 0; species < setup.SpeciesFleeWeights.Length; species++)
        {
            world.Add(new DataEntity(["boids.species"], [],
                ("species.index", species),
                ("species.colorR", 200),
                ("species.colorG", 200),
                ("species.colorB", 200),
                ("species.force.0", 0.0),
                ("species.force.1", 0.0),
                ("species.fleeWeight", setup.SpeciesFleeWeights[species])));
        }

        world.Add(new DataEntity(["boids.predator"], [],
            ("predator.positionX", setup.WorldSize * 0.5f),
            ("predator.positionY", setup.WorldSize * 0.5f),
            ("predator.velocityX", 0.0),
            ("predator.velocityY", 0.0),
            ("predator.maxSpeed", 84.0),
            ("predator.maxForce", 46.0),
            ("predator.fleeRadius", setup.PredatorFleeRadius),
            ("predator.consumePerSecond", setup.PredatorConsumePerSecond),
            ("predator.consumeRadius", setup.PredatorConsumeRadius),
            ("predator.energy", setup.PredatorEnergy),
            ("predator.energyDrainPerSecond", setup.PredatorEnergyDrainPerSecond),
            ("predator.energyPerStock", setup.PredatorEnergyPerStock),
            ("predator.satedThreshold", setup.PredatorSatedThreshold),
            ("predator.restSpeedFactor", setup.PredatorRestSpeedFactor),
            ("predator.boidEnergyDrainPerSecond", setup.PredatorBoidEnergyDrainPerSecond)));

        if (setup.AddFood)
        {
            world.Add(new DataEntity(["food.patch"], [],
                ("food.positionX", setup.WorldSize * 0.5f),
                ("food.positionY", setup.WorldSize * 0.5f),
                ("food.capacity", setup.FoodCapacity),
                ("food.stock", setup.FoodStock),
                ("food.regenPerSecond", 0.0),
                ("food.feedAmount", setup.FoodFeedAmount),
                ("food.feedRadius", setup.FoodFeedRadius)));
        }

        return world;
    }

    private sealed record Setup
    {
        public int Seed { get; init; } = 5;
        public int BoidCount { get; init; } = 64;
        public float WorldSize { get; init; } = 200.0f;
        public float StarvedSpeedFactor { get; init; } = 0.5f;
        public float EnergyDrainPerSecond { get; init; } = 0.015f;
        public float[] SpeciesFleeWeights { get; init; } = [1.0f, 1.0f];
        public float PredatorEnergy { get; init; } = 0.6f;
        public float PredatorEnergyDrainPerSecond { get; init; }
        public float PredatorEnergyPerStock { get; init; }
        public float PredatorSatedThreshold { get; init; } = 0.85f;
        public float PredatorRestSpeedFactor { get; init; } = 0.3f;
        public float PredatorBoidEnergyDrainPerSecond { get; init; }
        public float PredatorFleeRadius { get; init; } = 60.0f;
        public float PredatorConsumeRadius { get; init; } = 20.0f;
        public float PredatorConsumePerSecond { get; init; } = 500.0f;
        public bool AddFood { get; init; }
        public float FoodStock { get; init; } = 1000.0f;
        public float FoodCapacity { get; init; } = 1000.0f;
        public float FoodFeedAmount { get; init; } = 0.2f;
        public float FoodFeedRadius { get; init; } = 20.0f;
    }
}
