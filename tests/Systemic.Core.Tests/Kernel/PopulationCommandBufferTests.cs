using System.Numerics;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationCommandBufferTests
{
    private static readonly PopulationColumnKey<Vector2> s_positionKey =
        new("command.position", 1, PopulationCodecs.Vector2);
    private static readonly PopulationColumnKey<float> s_energyKey =
        new("command.energy", 1, PopulationCodecs.Single);

    [Fact]
    public void Spawn_batch_initializes_current_and_next_buffers_and_single_columns()
    {
        var world = new World(seed: 91, valueScale: 1);
        var population = world.DefinePopulation<CommandParticle>(enableHandles: true);
        var position = population.DoubleBufferedColumn(s_positionKey);
        var energy = population.Column(s_energyKey);
        var commands = new PopulationCommandBuffer<CommandParticle>(population);
        commands.Reserve(spawnCapacity: 2, removalCapacity: 2);
        population.Reserve(2);
        commands.Spawn([
            new CommandParticle(new Vector2(2, 3), 0.25f),
            new CommandParticle(new Vector2(7, 11), 0.75f)
        ]);

        var result = commands.Flush();

        Assert.Equal(new PopulationRange(0, 2), result.SpawnedRows);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal([new Vector2(2, 3), new Vector2(7, 11)], position.Current.ToArray());
        Assert.Equal([new Vector2(2, 3), new Vector2(7, 11)], position.EditNext(0, 2).ToArray());
        Assert.Equal([0.25f, 0.75f], energy.ReadOnlySpan.ToArray());
        Assert.Equal(2, population.Count);
        Assert.True(population.TryResolve(population.GetHandle(1), out var secondRow));
        Assert.Equal(1, secondRow);
    }

    [Fact]
    public void Batch_removals_use_the_original_rows_and_stale_handles_are_ignored()
    {
        var world = new World(seed: 92, valueScale: 1);
        var population = world.DefinePopulation<CommandParticle>(enableHandles: true);
        var position = population.DoubleBufferedColumn(s_positionKey);
        var commands = new PopulationCommandBuffer<CommandParticle>(population);
        commands.Reserve(spawnCapacity: 4, removalCapacity: 4);
        population.Reserve(4);
        commands.Spawn([
            new CommandParticle(new Vector2(1, 0), 0),
            new CommandParticle(new Vector2(2, 0), 0),
            new CommandParticle(new Vector2(3, 0), 0),
            new CommandParticle(new Vector2(4, 0), 0)
        ]);
        commands.Flush();
        var first = population.GetHandle(0);
        var third = population.GetHandle(2);
        var fourth = population.GetHandle(3);

        commands.RemoveRow(1);
        commands.Remove(third);
        commands.Remove(third);
        var result = commands.Flush();

        Assert.Equal(2, result.RemovedCount);
        Assert.Equal([new Vector2(1, 0), new Vector2(4, 0)], position.Current.ToArray());
        Assert.True(population.TryResolve(first, out var firstRow));
        Assert.Equal(0, firstRow);
        Assert.True(population.TryResolve(fourth, out var fourthRow));
        Assert.Equal(1, fourthRow);
        Assert.False(population.TryResolve(third, out _));

        commands.Remove(third);
        Assert.Equal(0, commands.Flush().RemovedCount);
        Assert.Equal(2, population.Count);
    }

    [Fact]
    public void Invalid_removal_aborts_the_entire_batch_without_spawning_rows()
    {
        var world = new World(seed: 93, valueScale: 1);
        var population = world.DefinePopulation<CommandParticle>();
        population.AddRange(1);
        var commands = new PopulationCommandBuffer<CommandParticle>(population);
        commands.Reserve(spawnCapacity: 1, removalCapacity: 1);
        commands.Spawn([new CommandParticle(new Vector2(5, 8), 1)]);
        commands.RemoveRow(population.Count);

        Assert.Throws<ArgumentOutOfRangeException>(() => commands.Flush());
        Assert.Equal(1, population.Count);

        commands.Clear();
        Assert.Equal(1, population.Count);
    }

    [Fact]
    public void Schema_failure_rolls_back_spawned_rows_and_keeps_the_batch_uncommitted()
    {
        var world = new World(seed: 94, valueScale: 1);
        var population = world.DefinePopulation<CommandParticle>();
        var position = population.DoubleBufferedColumn(s_positionKey);
        var commands = new PopulationCommandBuffer<CommandParticle>(population);
        commands.Reserve(spawnCapacity: 1, removalCapacity: 1);
        commands.Spawn([new CommandParticle(new Vector2(6, 9), -1)]);

        Assert.Throws<InvalidDataException>(() => commands.Flush());
        Assert.Equal(0, population.Count);
        Assert.Empty(position.Current.ToArray());
    }

    [Fact]
    public void Reserved_warmed_spawn_and_remove_batches_do_not_allocate()
    {
        var world = new World(seed: 95, valueScale: 1);
        var population = world.DefinePopulation<CommandParticle>(enableHandles: true);
        _ = population.DoubleBufferedColumn(s_positionKey);
        _ = population.Column(s_energyKey);
        population.Reserve(4);
        var commands = new PopulationCommandBuffer<CommandParticle>(population);
        commands.Reserve(spawnCapacity: 1, removalCapacity: 1);

        for (var iteration = 0; iteration < 8; iteration++)
        {
            commands.Spawn([new CommandParticle(new Vector2(iteration, 1), 1)]);
            commands.Flush();
            commands.RemoveRow(0);
            commands.Flush();
        }

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var lastSpawnedCount = 0;
        var lastRemovedCount = 0;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            commands.Spawn([new CommandParticle(new Vector2(iteration, 1), 1)]);
            lastSpawnedCount = commands.Flush().SpawnedRows.Length;
            commands.RemoveRow(0);
            lastRemovedCount = commands.Flush().RemovedCount;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Assert.Equal(1, lastSpawnedCount);
        Assert.Equal(1, lastRemovedCount);
        Assert.Equal(0, population.Count);
    }

    private readonly struct CommandParticle(Vector2 position, float energy)
        : IPopulationKind, IPopulationSchema<CommandParticle>
    {
        public static string Id => "test.command-particles";
        public static int Version => 1;
        public Vector2 Position { get; } = position;
        public float Energy { get; } = energy;

        public static void InitializeRange(
            Population<CommandParticle> population,
            PopulationRange range,
            ReadOnlySpan<CommandParticle> values)
        {
            var position = population.DoubleBufferedColumn(s_positionKey)
                .EditBothBuffers(range.Start, range.Length);
            var energy = population.Column(s_energyKey).EditRange(range.Start, range.Length);
            for (var index = 0; index < values.Length; index++)
            {
                if (values[index].Energy < 0)
                {
                    throw new InvalidDataException("Test schema rejects negative energy.");
                }

                position.Current[index] = values[index].Position;
                position.Next[index] = values[index].Position;
                energy[index] = values[index].Energy;
            }
        }
    }
}
