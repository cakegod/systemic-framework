using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

/// <summary>
/// Fixed-point positions in a population column must round trip and advance as exact bits. Every
/// assertion compares raw Q32.32 values, because a decimal comparison would hide exactly the drift
/// this storage exists to prevent.
/// </summary>
public sealed class FixedPointColumnTests
{
    private static readonly PopulationColumnKey<FixedVector2> s_positionKey =
        new("test.fixed.position", 1, FixedVector2Codec.Instance);

    private static readonly PopulationColumnKey<FixedVector2> s_velocityKey =
        new("test.fixed.velocity", 1, FixedVector2Codec.Instance);

    private static readonly PopulationColumnKey<Fixed64> s_energyKey =
        new("test.fixed.energy", 1, Fixed64Codec.Instance);

    private const long OneRaw = 1L << 32;

    [Fact]
    public void Codecs_round_trip_exact_bits_including_the_limits()
    {
        Fixed64[] scalars = [Fixed64.FromRaw(1), Fixed64.FromRaw(-1), Fixed64.FromRaw(long.MinValue),
            Fixed64.FromRaw(long.MaxValue), Fixed64.Zero];
        var scalarBytes = new byte[Fixed64Codec.Instance.GetEncodedLength(scalars.Length)];
        Fixed64Codec.Instance.Encode(scalars, scalarBytes);
        Assert.Equal([1, 0, 0, 0, 0, 0, 0, 0], scalarBytes[..8]);
        Assert.Equal([255, 255, 255, 255, 255, 255, 255, 255], scalarBytes[8..16]);
        var decodedScalars = new Fixed64[scalars.Length];
        Fixed64Codec.Instance.Decode(scalarBytes, decodedScalars);
        Assert.Equal(scalars, decodedScalars);
        Assert.Throws<ArgumentException>(() => Fixed64Codec.Instance.Decode(scalarBytes.AsSpan(1), decodedScalars));

        FixedVector2[] vectors = [new(Fixed64.FromRaw(3), Fixed64.FromRaw(-4)),
            new(Fixed64.FromRaw(long.MinValue), Fixed64.FromRaw(long.MaxValue))];
        var vectorBytes = new byte[FixedVector2Codec.Instance.GetEncodedLength(vectors.Length)];
        FixedVector2Codec.Instance.Encode(vectors, vectorBytes);
        var decodedVectors = new FixedVector2[vectors.Length];
        FixedVector2Codec.Instance.Decode(vectorBytes, decodedVectors);
        Assert.Equal(vectors, decodedVectors);
    }

    [Fact]
    public void Fixed_columns_round_trip_through_the_population_save_state()
    {
        var source = new World(seed: 95, valueScale: 1);
        var sourcePopulation = source.DefinePopulation<CrowdKind>();
        var positions = sourcePopulation.Column(s_positionKey);
        var energy = sourcePopulation.Column(s_energyKey);
        sourcePopulation.Reserve(2);
        sourcePopulation.AddRange(2);
        sourcePopulation.WithAccess(new PopulationRange(0, 2), (Positions: positions, Energy: energy),
            static ((PopulationColumn<FixedVector2> Positions, PopulationColumn<Fixed64> Energy) columns,
                scoped ref PopulationAccessScope<CrowdKind> scope) =>
            {
                var position = scope.EditRange(columns.Positions);
                var energy = scope.EditRange(columns.Energy);
                position[0] = new FixedVector2((Fixed64)1.5m, (Fixed64)(-2.25m));
                position[1] = new FixedVector2((Fixed64)(-0.5m), (Fixed64)0.125m);
                energy[0] = (Fixed64)0.75m;
                energy[1] = (Fixed64)(-0.75m);
            });
        var state = source.CapturePopulationState();

        var target = new World(seed: 95, valueScale: 1);
        var targetPopulation = target.DefinePopulation<CrowdKind>();
        targetPopulation.Column(s_positionKey);
        targetPopulation.Column(s_energyKey);
        target.RestorePopulationState(state);

        Assert.Equal(6_442_450_944L, Read(targetPopulation, s_positionKey, 0).X.RawValue);
        Assert.Equal(-9_663_676_416L, Read(targetPopulation, s_positionKey, 0).Y.RawValue);
        Assert.Equal(-2_147_483_648L, Read(targetPopulation, s_positionKey, 1).X.RawValue);
        Assert.Equal(536_870_912L, Read(targetPopulation, s_positionKey, 1).Y.RawValue);
        Assert.Equal(3_221_225_472L, Read(targetPopulation, s_energyKey, 0).RawValue);
        Assert.Equal(-3_221_225_472L, Read(targetPopulation, s_energyKey, 1).RawValue);
    }

    [Fact]
    public void Movement_after_three_ticks_matches_the_exact_expected_bits()
    {
        var world = new World(seed: 96, valueScale: 1);
        var population = Create(world, 1, out var positions, out var velocities);

        for (var tick = 0; tick < 3; tick++)
        {
            population.WithAccess(new PopulationRange(0, 1), (Positions: positions, Velocities: velocities),
                static ((PopulationColumn<FixedVector2> Positions, PopulationColumn<FixedVector2> Velocities) columns,
                    scoped ref PopulationAccessScope<CrowdKind> scope) =>
                {
                    var position = scope.EditRange(columns.Positions);
                    var velocity = scope.ReadAll(columns.Velocities);
                    position[0] += velocity[0];
                });
        }

        var final = Read(population, s_positionKey, 0);
        // 0.5 + 3 * 0.25 = 1.25 and -1.25 + 3 * 0.5 = 0.25, exactly.
        Assert.Equal(5_368_709_120L, final.X.RawValue);
        Assert.Equal(1_073_741_824L, final.Y.RawValue);
        Assert.Equal(6_979_321_856L, final.LengthSquared().RawValue);
    }

    [Fact]
    public void A_long_movement_run_stays_exact_instead_of_drifting()
    {
        var world = new World(seed: 97, valueScale: 1);
        var population = Create(world, 1, out var positions, out var velocities);

        // 0.1 is not representable in binary or decimal Q32.32, so the point of the run is that the
        // accumulated bits are the same every time, not that they equal a decimal ideal.
        const long steps = 10_000;
        for (var step = 0; step < steps; step++)
        {
            population.WithAccess(new PopulationRange(0, 1), (Positions: positions, Velocities: velocities),
                static ((PopulationColumn<FixedVector2> Positions, PopulationColumn<FixedVector2> Velocities) columns,
                    scoped ref PopulationAccessScope<CrowdKind> scope) =>
                {
                    var position = scope.EditRange(columns.Positions);
                    var velocity = scope.ReadAll(columns.Velocities);
                    position[0] += velocity[0];
                });
        }

        var final = Read(population, s_positionKey, 0);
        // Golden bits: 0.5 + 10,000 * 0.25 = 2,500.5, and -1.25 + 10,000 * 0.5 = 4,998.75.
        Assert.Equal(10_739_565_723_648L, final.X.RawValue);
        Assert.Equal(21_469_467_770_880L, final.Y.RawValue);
        Assert.Equal(0, final.X.RawValue % (OneRaw / 4));
    }

    [Fact]
    public void Movement_out_of_range_fails_loudly_instead_of_saturating()
    {
        var world = new World(seed: 98, valueScale: 1);
        var population = Create(world, 1, out var positions, out var velocities);
        population.WithAccess(new PopulationRange(0, 1), (Positions: positions, Velocities: velocities),
            static ((PopulationColumn<FixedVector2> Positions, PopulationColumn<FixedVector2> Velocities) columns,
                scoped ref PopulationAccessScope<CrowdKind> scope) =>
            {
                var position = scope.EditRange(columns.Positions);
                position[0] = new FixedVector2(Fixed64.FromRaw(long.MaxValue), Fixed64.Zero);
            });

        Assert.Throws<OverflowException>(() =>
            population.WithAccess(new PopulationRange(0, 1), (Positions: positions, Velocities: velocities),
                static ((PopulationColumn<FixedVector2> Positions, PopulationColumn<FixedVector2> Velocities) columns,
                    scoped ref PopulationAccessScope<CrowdKind> scope) =>
                {
                    var position = scope.EditRange(columns.Positions);
                    var velocity = scope.ReadAll(columns.Velocities);
                    position[0] += velocity[0];
                }));
    }

    private static Population<CrowdKind> Create(World world, int count,
        out PopulationColumn<FixedVector2> positions, out PopulationColumn<FixedVector2> velocities)
    {
        var population = world.DefinePopulation<CrowdKind>();
        positions = population.Column(s_positionKey);
        velocities = population.Column(s_velocityKey);
        population.Reserve(count);
        population.AddRange(count);
        population.WithAccess(new PopulationRange(0, count), (Positions: positions, Velocities: velocities),
            static ((PopulationColumn<FixedVector2> Positions, PopulationColumn<FixedVector2> Velocities) columns,
                scoped ref PopulationAccessScope<CrowdKind> scope) =>
            {
                var position = scope.EditRange(columns.Positions);
                var velocity = scope.EditRange(columns.Velocities);
                for (var row = 0; row < position.Length; row++)
                {
                    position[row] = new FixedVector2((Fixed64)0.5m, (Fixed64)(-1.25m));
                    velocity[row] = new FixedVector2((Fixed64)0.25m, (Fixed64)0.5m);
                }
            });
        return population;
    }

    private static T Read<T>(Population<CrowdKind> population, PopulationColumnKey<T> key, int row)
        where T : unmanaged
    {
        var values = new T[1];
        population.WithAccess(new PopulationRange(row, 1),
            (Column: population.Column(key), Row: row, Values: values),
            static ((PopulationColumn<T> Column, int Row, T[] Values) state,
                scoped ref PopulationAccessScope<CrowdKind> scope) =>
                state.Values[0] = scope.ReadAll(state.Column)[state.Row]);
        return values[0];
    }

    private readonly struct CrowdKind : IPopulationKind
    {
        public static string Id => "test.fixed-crowd";

        public static int Version => 1;
    }
}
