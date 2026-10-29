using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using Systemic.Kernel;
using Systemic.Host;
using Systemic.Rules;

namespace Boids.MonoGame;

public sealed class BoidFlockSystem : ISystemicSystem, ISystemicReusableTransactionalState, IDisposable
{
    private const int MaximumNeighborsPerBoid = 3;
    private const int MaximumCandidatesPerBoid = 12;
    private const int MaximumFoodPatches = 32;
    private const int MaximumSpecies = 8;
    private const float MaximumEnergy = 1.0f;
    private const float HungryEnergy = 0.65f;
    private const float FoodSteeringWeight = 1.4f;
    private const float SatedDragPerSecond = 0.6f;
    private static readonly ImmutableArray<SystemAccess> s_access =
    [
        SystemAccess.WriteWorld,
        BoidPopulation.WritePosition,
        BoidPopulation.WriteVelocity,
        BoidPopulation.WriteEnergy,
        BoidPopulation.ReadSpecies
    ];

    private readonly int _flockEntityId;
    private readonly int _population;
    private readonly float _worldWidth;
    private readonly float _worldHeight;
    private readonly float _neighborRadius;
    private readonly float _collisionRadius;
    private readonly float _collisionStrength;
    private readonly float _friction;
    private readonly float _particleRadius;
    private readonly float _maximumSpeed;
    private readonly float _maximumForce;
    private readonly float _deltaTime;
    private readonly float _starvedSpeedFactor;
    private readonly float _energyDrainPerSecond;
    private readonly Action _restorePredatorTarget;
    private readonly Population<Boid> _boids;
    private readonly BoidPopulation.Columns _columns;
    private readonly Vector2[] _positionSnapshot;
    private readonly Vector2[] _velocitySnapshot;
    private readonly float[] _energySnapshot;
    private readonly int[] _speciesSnapshot;
    private bool _populationSnapshotsDirty = true;
    private readonly PopulationSpatialGrid2D _spatialGrid;
    private int _predatorTargetBeforeStep = -1;
    private readonly float[] _speciesFleeWeights;
    private readonly float _predatorConsumeRadiusSquared;
    private readonly FoodPatch[] _foodPatches;
    private readonly Predator _predator;
    private readonly ParticleSpecies[] _species;
    private readonly ParticleSpeciesView[] _speciesViews;
    private readonly float[] _speciesForceMatrix;
    private readonly FoodPatchView[] _foodViews;
    private readonly float[] _foodStocks;
    private readonly int[] _foodRequestCounts;
    private readonly float[] _foodFeedShares;
    private readonly int[] _feedingPatchByBoid;
    private Vector2 _predatorPosition;
    private Vector2 _predatorVelocity;
    private float _predatorFleeRadius;
    private bool _predatorSated;
    private float _hungryFraction;
    private int _predatorTargetFoodIndex = -1;
    private PredatorView _predatorView;
    private bool _disposed;
    private readonly Stack<RollbackSnapshot> _availableSnapshots = new();

    private static readonly PopulationAccessAction<Boid, PopulationStepState> s_rebuildSpatialGrid =
        static (PopulationStepState state, scoped ref PopulationAccessScope<Boid> access) =>
        {
            var positions = access.ReadCurrent(state.Columns.Position);
            state.System._spatialGrid.Rebuild(positions);
        };

    private static readonly PopulationRangeAction<Boid, PopulationStepState> s_advanceRange =
        static (PopulationStepState state, int partition, scoped ref PopulationAccessScope<Boid> access) =>
            state.System.AdvanceRange(state.Tick, state.Columns, ref access);

    private static readonly PopulationAccessAction<Boid, PopulationUpdateState> s_aggregateFeeding =
        static (PopulationUpdateState state, scoped ref PopulationAccessScope<Boid> access) =>
            state.System.AggregateFeeding(
                state.World,
                state.Context,
                access.EditNext(state.Columns.Position),
                access.EditNext(state.Columns.Velocity),
                access.EditNext(state.Columns.Energy),
                access.ReadAll(state.Columns.Species));

    public BoidFlockSystem(World world, int flockEntityId, int seed, int tickRateHz)
    {
        ArgumentNullException.ThrowIfNull(world);
        if (tickRateHz is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(tickRateHz));
        }

        var flock = world.ById(flockEntityId);
        if (flock is not { ToRemove: false } || !world.HasTag(flock, "boids.flock"))
        {
            throw new ArgumentException("The boid system needs one live boids.flock entity.", nameof(flockEntityId));
        }

        _flockEntityId = flockEntityId;
        _population = ReadPositiveInt(world, flock, "boids.population");
        _worldWidth = ReadPositiveFloat(world, flock, "boids.worldWidth");
        _worldHeight = ReadPositiveFloat(world, flock, "boids.worldHeight");
        _neighborRadius = ReadPositiveFloat(world, flock, "boids.neighborRadius");
        _collisionRadius = ReadPositiveFloat(world, flock, "boids.collisionRadius");
        _collisionStrength = ReadPositiveFloat(world, flock, "boids.collisionStrength");
        _friction = ReadNonNegativeFloat(world, flock, "boids.friction");
        _particleRadius = ReadPositiveFloat(world, flock, "boids.particleRadius");
        _maximumSpeed = ReadPositiveFloat(world, flock, "boids.maxSpeed");
        _maximumForce = ReadPositiveFloat(world, flock, "boids.maxForce");
        _starvedSpeedFactor = ReadUnitFloat(world, flock, "boids.starvedSpeedFactor", allowZero: false);
        _energyDrainPerSecond = ReadNonNegativeFloat(world, flock, "boids.energyDrainPerSecond");
        if (_collisionRadius > _neighborRadius)
        {
            throw new ArgumentException("The collision radius cannot exceed the interaction radius.", nameof(world));
        }

        _species = ReadSpecies(world);
        _speciesFleeWeights = [.. _species.Select(species => species.FleeWeight)];
        _speciesViews = [.. _species.Select(species => new ParticleSpeciesView(species.Index, species.Red, species.Green, species.Blue))];
        _speciesForceMatrix = new float[_species.Length * _species.Length];
        for (var source = 0; source < _species.Length; source++)
        {
            Array.Copy(_species[source].Forces, 0, _speciesForceMatrix, source * _species.Length, _species.Length);
        }

        _deltaTime = 1.0f / tickRateHz;
        _spatialGrid = new PopulationSpatialGrid2D(
            new Bounds2(_worldWidth * 0.5f, _worldHeight * 0.5f, _worldWidth * 0.5f, _worldHeight * 0.5f),
            _neighborRadius,
            wrapX: true,
            wrapY: true);
        _spatialGrid.Reserve(_population);
        RandomizeFoodPatchPositions(world, _worldWidth, _worldHeight, seed);
        _foodPatches = ReadFoodPatches(world, _worldWidth, _worldHeight);
        _predator = ReadPredator(world, _worldWidth, _worldHeight);
        _boids = BoidPopulation.Define(world);
        _columns = BoidPopulation.Bind(_boids);
        _positionSnapshot = new Vector2[_population];
        _velocitySnapshot = new Vector2[_population];
        _energySnapshot = new float[_population];
        _speciesSnapshot = new int[_population];
        _boids.Reserve(_population);
        if (_boids.AddRange(_population).Start != 0)
        {
            throw new InvalidOperationException("A new boid population must begin at row zero.");
        }
        _predatorConsumeRadiusSquared = _predator.ConsumeRadius * _predator.ConsumeRadius;
        _foodViews = new FoodPatchView[_foodPatches.Length];
        _foodStocks = new float[_foodPatches.Length];
        _foodRequestCounts = new int[_foodPatches.Length];
        _foodFeedShares = new float[_foodPatches.Length];
        _feedingPatchByBoid = new int[_population];
        InitializeFoodViews(world);
        InitializePredatorView(world);
        Initialize(seed);
        _restorePredatorTarget = RestorePredatorTarget;
    }

    public string Id => "sample.boids.flock";

    public int Order => 100;

    public IReadOnlyList<SystemAccess> Access => s_access;

    public int Population => _population;

    public float WorldWidth => _worldWidth;

    public float WorldHeight => _worldHeight;

    public float ParticleRadius => _particleRadius;

    public ReadOnlySpan<float> Energies
    {
        get
        {
            RefreshPopulationSnapshots();
            return _energySnapshot;
        }
    }

    public float HungryFraction => _hungryFraction;

    public float StarvedSpeedFactor => _starvedSpeedFactor;

    public ReadOnlySpan<Vector2> Positions
    {
        get
        {
            RefreshPopulationSnapshots();
            return _positionSnapshot;
        }
    }

    public ReadOnlySpan<Vector2> Velocities
    {
        get
        {
            RefreshPopulationSnapshots();
            return _velocitySnapshot;
        }
    }

    public ReadOnlySpan<int> SpeciesByBoid
    {
        get
        {
            RefreshPopulationSnapshots();
            return _speciesSnapshot;
        }
    }

    public ReadOnlySpan<ParticleSpeciesView> Species => _speciesViews;

    public ReadOnlySpan<FoodPatchView> FoodPatches => _foodViews;

    public PredatorView PredatorState => _predatorView;

    public JsonElement CaptureState() => JsonSerializer.SerializeToElement(new
    {
        version = 1,
        predatorTargetFoodEntityId = _predatorTargetFoodIndex is >= 0 and < MaximumFoodPatches
            && _predatorTargetFoodIndex < _foodPatches.Length
                ? _foodPatches[_predatorTargetFoodIndex].EntityId
                : (int?)null
    });

    public ISystemicTransactionSnapshot CaptureTransactionSnapshot()
    {
        var snapshot = _availableSnapshots.Count == 0 ? new RollbackSnapshot(this) : _availableSnapshots.Pop();
        snapshot.Capture();
        return snapshot;
    }

    private sealed class RollbackSnapshot
        : ISystemicTransactionSnapshot
    {
        private readonly BoidFlockSystem _owner;
        private readonly FoodPatchView[] _food;
        private int _target;
        private float _hungry, _fleeRadius;
        private bool _sated;
        private Vector2 _position, _velocity;
        private PredatorView _predator;

        public RollbackSnapshot(BoidFlockSystem owner)
        {
            _owner = owner;
            _food = new FoodPatchView[owner._foodViews.Length];
            OnCommit = Commit;
            OnRollback = Rollback;
        }

        public Action OnCommit { get; }
        public Action OnRollback { get; }

        public void Capture()
        {
            _target = _owner._predatorTargetFoodIndex;
            _hungry = _owner._hungryFraction;
            _position = _owner._predatorPosition;
            _velocity = _owner._predatorVelocity;
            _fleeRadius = _owner._predatorFleeRadius;
            _sated = _owner._predatorSated;
            _predator = _owner._predatorView;
            _owner._foodViews.CopyTo(_food, 0);
        }

        private void Commit() => _owner._availableSnapshots.Push(this);

        private void Rollback()
        {
            _owner._predatorTargetFoodIndex = _target;
            _owner._hungryFraction = _hungry;
            _owner._predatorPosition = _position;
            _owner._predatorVelocity = _velocity;
            _owner._predatorFleeRadius = _fleeRadius;
            _owner._predatorSated = _sated;
            _owner._predatorView = _predator;
            _food.CopyTo(_owner._foodViews, 0);
            _owner._populationSnapshotsDirty = true;
            _owner._availableSnapshots.Push(this);
        }
    }

    private readonly record struct PopulationStepState(
        BoidFlockSystem System,
        BoidPopulation.Columns Columns,
        int Tick);

    private readonly record struct PopulationUpdateState(
        BoidFlockSystem System,
        BoidPopulation.Columns Columns,
        ReadOnlyWorldView World,
        SystemicSystemContext Context);

    private readonly record struct PopulationSnapshotState(
        BoidPopulation.Columns Columns,
        Vector2[] Positions,
        Vector2[] Velocities,
        float[] Energies,
        int[] Species);

    public void RestoreState(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("version"u8, out var versionElement)
            || !versionElement.TryGetInt32(out var version)
            || version != 1
            || !state.TryGetProperty("predatorTargetFoodEntityId"u8, out var targetElement))
        {
            throw new InvalidDataException("Saved boid flock state has an unsupported or malformed shape.");
        }

        if (targetElement.ValueKind == JsonValueKind.Null)
        {
            _predatorTargetFoodIndex = -1;
            _populationSnapshotsDirty = true;
            return;
        }

        if (!targetElement.TryGetInt32(out var targetEntityId))
        {
            throw new InvalidDataException("Saved boid predator target must be a food entity ID or null.");
        }

        var targetIndex = Array.FindIndex(_foodPatches, patch => patch.EntityId == targetEntityId);
        if ((uint)targetIndex >= (uint)_foodPatches.Length)
        {
            throw new InvalidDataException(
                $"Saved boid predator target references unknown food patch entity {targetEntityId}.");
        }

        _predatorTargetFoodIndex = targetIndex;
        _populationSnapshotsDirty = true;
    }

    private void RefreshPopulationSnapshots()
    {
        if (!_populationSnapshotsDirty)
        {
            return;
        }

        var state = new PopulationSnapshotState(
            _columns, _positionSnapshot, _velocitySnapshot, _energySnapshot, _speciesSnapshot);
        _boids.WithAccess(new PopulationRange(0, _boids.Count), state,
            static (PopulationSnapshotState snapshot, scoped ref PopulationAccessScope<Boid> access) =>
            {
                access.ReadCurrent(snapshot.Columns.Position).CopyTo(snapshot.Positions);
                access.ReadCurrent(snapshot.Columns.Velocity).CopyTo(snapshot.Velocities);
                access.ReadCurrent(snapshot.Columns.Energy).CopyTo(snapshot.Energies);
                access.ReadAll(snapshot.Columns.Species).CopyTo(snapshot.Species);
            });
        _populationSnapshotsDirty = false;
    }

    internal BoidPopulation.Columns Columns => _columns;

    internal void WithPopulationAccess<TState>(
        TState state,
        PopulationAccessAction<Boid, TState> callback) =>
        _boids.WithAccess(new PopulationRange(0, _population), state, callback);

    public static bool WantsFood(float energy) => energy < HungryEnergy;

    public static float EnergySpeedFactor(float energy, float starvedSpeedFactor) =>
        starvedSpeedFactor + (1.0f - starvedSpeedFactor) * Math.Clamp(energy, 0.0f, 1.0f);

    public static float HungerFactor(float energy, float restFactor) =>
        restFactor + (1.0f - restFactor) * (1.0f - Math.Clamp(energy, 0.0f, 1.0f));

    public void Step(SystemicSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _predatorTargetBeforeStep = _predatorTargetFoodIndex;
        if (context.IsScheduledExecution)
            context.OnRollback(_restorePredatorTarget);
        StepCore(context.World, context);
    }

    private void StepCore(ReadOnlyWorldView world, SystemicSystemContext context)
    {
        if (!world.ContainsEntity(_flockEntityId))
        {
            return;
        }

        _populationSnapshotsDirty = true;
        RegenerateFood(world);
        UpdatePredator(world, context);
        var stepState = new PopulationStepState(this, _columns, context.Tick);
        _boids.WithAccess(new PopulationRange(0, _population), stepState, s_rebuildSpatialGrid);
        context.ForEachPopulationRange(_boids, stepState, s_advanceRange);
        _boids.WithAccess(
            new PopulationRange(0, _population),
            new PopulationUpdateState(this, _columns, world, context),
            s_aggregateFeeding);

        // Swapping is a structural population operation, so complete every scoped view first.
        _columns.Position.Swap();
        _columns.Velocity.Swap();
        _columns.Energy.Swap();

        var fact = new SimulationFact("boids.flock.updated", _flockEntityId, _flockEntityId, Amount: _population);
        context.Publish(fact);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }

    private static int ReadPositiveInt(World world, Entity entity, string property)
    {
        var value = ReadPositiveFloat(world, entity, property);
        if (value > 1_000_000 || MathF.Truncate(value) != value)
        {
            throw new ArgumentOutOfRangeException(property, "The flock population must be an integer no greater than one million.");
        }

        return (int)value;
    }

    private static float ReadPositiveFloat(World world, Entity entity, string property)
    {
        if (!entity.TryGetProperty(world, property, out var value)
            || !double.IsFinite(value) || value <= 0 || value > float.MaxValue)
        {
            throw new ArgumentException($"Flock entity {entity.Id} needs a finite positive '{property}' property.", nameof(entity));
        }

        return (float)value;
    }

    private static FoodPatch[] ReadFoodPatches(World world, float worldWidth, float worldHeight)
    {
        var entities = world.Entities
            .Where(entity => !entity.ToRemove && world.HasTag(entity, "food.patch"))
            .OrderBy(entity => entity.Id)
            .ToArray();
        if (entities.Length > MaximumFoodPatches)
        {
            throw new ArgumentOutOfRangeException(nameof(world),
                $"The boid sample supports at most {MaximumFoodPatches} food patches.");
        }

        return [.. entities.Select(entity =>
        {
            var x = ReadNonNegativeFloat(world, entity, "food.positionX");
            var y = ReadNonNegativeFloat(world, entity, "food.positionY");
            var capacity = ReadPositiveFloat(world, entity, "food.capacity");
            var stock = ReadNonNegativeFloat(world, entity, "food.stock");
            var regeneration = ReadNonNegativeFloat(world, entity, "food.regenPerSecond");
            var feedAmount = ReadPositiveFloat(world, entity, "food.feedAmount");
            var feedRadius = ReadPositiveFloat(world, entity, "food.feedRadius");

            if (x >= worldWidth || y >= worldHeight || stock > capacity)
            {
                throw new ArgumentOutOfRangeException(nameof(world),
                    $"Food patch '{entity.Id}' has an out-of-bounds position or stock above capacity.");
            }

            return new FoodPatch(entity.Id, new Vector2(x, y), capacity, regeneration, feedAmount, feedRadius);
        })];
    }

    private static ParticleSpecies[] ReadSpecies(World world)
    {
        var entities = world.Entities
            .Where(entity => !entity.ToRemove && world.HasTag(entity, "boids.species"))
            .Select(entity => (Entity: entity, Index: ReadNonNegativeInt(world, entity, "species.index", MaximumSpecies - 1)))
            .OrderBy(item => item.Index)
            .ToArray();
        if (entities.Length is < 2 or > MaximumSpecies)
        {
            throw new ArgumentOutOfRangeException(nameof(world),
                $"The sample needs 2 to {MaximumSpecies} boids.species entities.");
        }

        var species = new ParticleSpecies[entities.Length];
        for (var index = 0; index < entities.Length; index++)
        {
            var (entity, speciesIndex) = entities[index];
            if (speciesIndex != index)
            {
                throw new ArgumentException("Species indices must be unique and contiguous from zero.", nameof(world));
            }

            var forces = new float[entities.Length];
            for (var target = 0; target < forces.Length; target++)
            {
                forces[target] = ReadFiniteFloat(world, entity, $"species.force.{target}");
            }

            species[index] = new ParticleSpecies(
                index,
                ReadNonNegativeInt(world, entity, "species.colorR", 255),
                ReadNonNegativeInt(world, entity, "species.colorG", 255),
                ReadNonNegativeInt(world, entity, "species.colorB", 255),
                ReadNonNegativeFloat(world, entity, "species.fleeWeight"),
                forces);
        }

        return species;
    }

    private static void RandomizeFoodPatchPositions(World world, float worldWidth, float worldHeight, int seed)
    {
        var entities = world.Entities
            .Where(entity => !entity.ToRemove && world.HasTag(entity, "food.patch"))
            .OrderBy(entity => entity.Id);
        var random = new Random(unchecked(seed ^ 0x51ED270B));

        foreach (var entity in entities)
        {
            SetProperty(world, entity, "food.positionX", random.NextSingle() * worldWidth);
            SetProperty(world, entity, "food.positionY", random.NextSingle() * worldHeight);
        }
    }

    private static Predator ReadPredator(World world, float worldWidth, float worldHeight)
    {
        var entities = world.Entities
            .Where(entity => !entity.ToRemove && world.HasTag(entity, "boids.predator"))
            .OrderBy(entity => entity.Id)
            .ToArray();
        if (entities.Length != 1)
        {
            throw new ArgumentException("The sample needs exactly one live boids.predator entity.", nameof(world));
        }

        var entity = entities[0];
        var x = ReadNonNegativeFloat(world, entity, "predator.positionX");
        var y = ReadNonNegativeFloat(world, entity, "predator.positionY");
        if (x >= worldWidth || y >= worldHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(world), "The predator position must be inside the flock bounds.");
        }

        return new Predator(
            entity.Id,
            ReadPositiveFloat(world, entity, "predator.maxSpeed"),
            ReadPositiveFloat(world, entity, "predator.maxForce"),
            ReadPositiveFloat(world, entity, "predator.fleeRadius"),
            ReadPositiveFloat(world, entity, "predator.consumePerSecond"),
            ReadPositiveFloat(world, entity, "predator.consumeRadius"),
            ReadNonNegativeFloat(world, entity, "predator.energyDrainPerSecond"),
            ReadNonNegativeFloat(world, entity, "predator.energyPerStock"),
            ReadUnitFloat(world, entity, "predator.satedThreshold", allowZero: false),
            ReadUnitFloat(world, entity, "predator.restSpeedFactor", allowZero: false),
            ReadNonNegativeFloat(world, entity, "predator.boidEnergyDrainPerSecond"));
    }

    private static void SetProperty(World world, Entity entity, string property, float value)
    {
        if (!entity.TrySetProperty(world, property, value))
        {
            throw new ArgumentException($"Entity {entity.Id} needs a writable '{property}' property.", nameof(entity));
        }
    }

    private static float ReadFiniteFloat(World world, Entity entity, string property)
    {
        if (!entity.TryGetProperty(world, property, out var value)
            || !double.IsFinite(value) || Math.Abs(value) > float.MaxValue)
        {
            throw new ArgumentException($"Entity {entity.Id} needs a finite '{property}' property.", nameof(entity));
        }

        return (float)value;
    }

    private static float ReadFiniteFloat(ReadOnlyWorldView world, int entityId, string property)
    {
        if (!world.TryGetProperty(entityId, property, out var value)
            || !double.IsFinite(value) || Math.Abs(value) > float.MaxValue)
        {
            throw new ArgumentException($"Entity {entityId} needs a finite '{property}' property.", nameof(entityId));
        }

        return (float)value;
    }

    private static float ReadNonNegativeFloat(World world, Entity entity, string property)
    {
        if (!entity.TryGetProperty(world, property, out var value)
            || !double.IsFinite(value) || value < 0 || value > float.MaxValue)
        {
            throw new ArgumentException(
                $"Entity {entity.Id} needs a finite non-negative '{property}' property.", nameof(entity));
        }

        return (float)value;
    }

    private static float ReadNonNegativeFloat(ReadOnlyWorldView world, int entityId, string property)
    {
        if (!world.TryGetProperty(entityId, property, out var value)
            || !double.IsFinite(value) || value < 0 || value > float.MaxValue)
        {
            throw new ArgumentException(
                $"Entity {entityId} needs a finite non-negative '{property}' property.", nameof(entityId));
        }

        return (float)value;
    }

    private static float ReadUnitFloat(World world, Entity entity, string property, bool allowZero)
    {
        var value = ReadNonNegativeFloat(world, entity, property);
        if (value > 1.0f || (!allowZero && value == 0.0f))
        {
            throw new ArgumentException(
                $"Entity {entity.Id} needs a '{property}' value in {(allowZero ? "[0, 1]" : "(0, 1]")}.",
                nameof(entity));
        }

        return value;
    }

    private static float ReadUnitFloat(ReadOnlyWorldView world, int entityId, string property, bool allowZero)
    {
        var value = ReadNonNegativeFloat(world, entityId, property);
        if (value > 1.0f || (!allowZero && value == 0.0f))
        {
            throw new ArgumentException(
                $"Entity {entityId} needs a '{property}' value in {(allowZero ? "[0, 1]" : "(0, 1]")}.",
                nameof(entityId));
        }

        return value;
    }

    private static int ReadNonNegativeInt(World world, Entity entity, string property, int maximum)
    {
        var value = ReadNonNegativeFloat(world, entity, property);
        if (value > maximum || MathF.Truncate(value) != value)
        {
            throw new ArgumentOutOfRangeException(property,
                $"Entity {entity.Id} needs an integer '{property}' value between zero and {maximum}.");
        }

        return (int)value;
    }

    private void Initialize(int seed)
    {
        var random = new Random(seed);
        var values = new Boid[_population];
        for (var boid = 0; boid < _population; boid++)
        {
            var position = new Vector2(random.NextSingle() * _worldWidth, random.NextSingle() * _worldHeight);
            var angle = random.NextSingle() * MathF.Tau;
            var speed = _maximumSpeed * (0.3f + random.NextSingle() * 0.7f);
            var value = new Boid
            {
                Position = position,
                Velocity = new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * speed,
                Energy = 0.4f + random.NextSingle() * 0.6f,
                Species = random.Next(_species.Length)
            };
            values[boid] = value;
        }

        Boid.InitializeRange(_boids, new PopulationRange(0, _population), values);
    }

    private void RegenerateFood(ReadOnlyWorldView world)
    {
        for (var index = 0; index < _foodPatches.Length; index++)
        {
            var patch = _foodPatches[index];
            if (!world.TryGetProperty(patch.EntityId, "food.stock", out var currentStock))
            {
                _foodStocks[index] = 0.0f;
                continue;
            }

            if (index == _predatorTargetFoodIndex && currentStock <= 0.0)
            {
                _predatorTargetFoodIndex = -1;
            }

            _foodStocks[index] = Math.Min(patch.Capacity,
                (float)currentStock + patch.RegenerationPerSecond * _deltaTime);
        }
    }

    private void UpdatePredator(
        ReadOnlyWorldView world,
        SystemicSystemContext context)
    {
        if (!world.ContainsEntity(_predator.EntityId))
        {
            throw new InvalidOperationException("The systemic predator entity was removed while the flock is running.");
        }

        _predatorPosition = new Vector2(
            ReadNonNegativeFloat(world, _predator.EntityId, "predator.positionX"),
            ReadNonNegativeFloat(world, _predator.EntityId, "predator.positionY"));
        _predatorVelocity = new Vector2(
            ReadFiniteFloat(world, _predator.EntityId, "predator.velocityX"),
            ReadFiniteFloat(world, _predator.EntityId, "predator.velocityY"));

        var energy = ReadUnitFloat(world, _predator.EntityId, "predator.energy", allowZero: true);
        _predatorSated = energy >= _predator.SatedThreshold;
        var speed = _predator.MaximumSpeed * HungerFactor(energy, _predator.RestSpeedFactor);

        if (_predatorSated)
        {
            _predatorTargetFoodIndex = -1;
            _predatorVelocity *= MathF.Max(0.0f, 1.0f - SatedDragPerSecond * _deltaTime);
        }
        else
        {
            if (_predatorTargetFoodIndex < 0 || _foodStocks[_predatorTargetFoodIndex] <= 0.0f)
            {
                _predatorTargetFoodIndex = FindRichestStockedFoodPatch();
            }

            if (_predatorTargetFoodIndex >= 0)
            {
                var target = _foodPatches[_predatorTargetFoodIndex];
                var displacement = ShortestWrappedDisplacement(_predatorPosition, target.Position);
                var desiredVelocity = NormalizeOrZero(displacement) * speed;
                var acceleration = Limit(desiredVelocity - _predatorVelocity, _predator.MaximumForce);
                _predatorVelocity = Limit(_predatorVelocity + acceleration * _deltaTime, speed);
            }
        }

        _predatorPosition += _predatorVelocity * _deltaTime;
        _predatorPosition.X = WrapPosition(_predatorPosition.X, _worldWidth);
        _predatorPosition.Y = WrapPosition(_predatorPosition.Y, _worldHeight);

        if (!_predatorSated && _predatorTargetFoodIndex >= 0)
        {
            var target = _foodPatches[_predatorTargetFoodIndex];
            var displacement = ShortestWrappedDisplacement(_predatorPosition, target.Position);
            if (displacement.LengthSquared() <= _predatorConsumeRadiusSquared)
            {
                var consumed = Math.Min(_foodStocks[_predatorTargetFoodIndex],
                    _predator.ConsumePerSecond * _deltaTime);
                _foodStocks[_predatorTargetFoodIndex] -= consumed;
                energy = Math.Min(MaximumEnergy, energy + consumed * _predator.EnergyPerStock);
            }
        }

        energy = Math.Max(0.0f, energy - _predator.EnergyDrainPerSecond * _deltaTime);
        _predatorFleeRadius = _predator.FleeRadius * HungerFactor(energy, _predator.RestSpeedFactor);

        SetProperty(context, _predator.EntityId, "predator.energy", energy);
        SetProperty(context, _predator.EntityId, "predator.positionX", _predatorPosition.X);
        SetProperty(context, _predator.EntityId, "predator.positionY", _predatorPosition.Y);
        SetProperty(context, _predator.EntityId, "predator.velocityX", _predatorVelocity.X);
        SetProperty(context, _predator.EntityId, "predator.velocityY", _predatorVelocity.Y);
        _predatorView = new PredatorView(_predatorPosition, _predatorFleeRadius, energy, _predatorSated);
    }

    private static void SetProperty(
        SystemicSystemContext context,
        int entityId,
        string property,
        double value)
    {
        if (context.IsScheduledExecution)
        {
            context.Commands.SetProperty(entityId, property, value);
            return;
        }

        var world = context.GetWritableWorld();
        var entity = world.ById(entityId)
            ?? throw new InvalidOperationException($"Entity {entityId} no longer exists.");
        context.SetProperty(entity, property, value);
    }

    private int FindRichestStockedFoodPatch()
    {
        var richestPatch = -1;
        var richestStock = 0.0f;
        for (var index = 0; index < _foodPatches.Length; index++)
        {
            if (_foodStocks[index] > richestStock)
            {
                richestPatch = index;
                richestStock = _foodStocks[index];
            }
        }

        return richestPatch;
    }

    private void RestorePredatorTarget() => _predatorTargetFoodIndex = _predatorTargetBeforeStep;

    private void InitializeFoodViews(World world)
    {
        for (var index = 0; index < _foodPatches.Length; index++)
        {
            var patch = _foodPatches[index];
            var stock = world.ById(patch.EntityId) is { } entity
                && entity.TryGetProperty(world, "food.stock", out var value)
                ? (float)value
                : 0.0f;
            _foodStocks[index] = stock;
            _foodViews[index] = new FoodPatchView(patch.Position, stock, patch.Capacity);
        }
    }

    private void InitializePredatorView(World world)
    {
        var entity = world.ById(_predator.EntityId)!;
        _predatorPosition = new Vector2(
            ReadNonNegativeFloat(world, entity, "predator.positionX"),
            ReadNonNegativeFloat(world, entity, "predator.positionY"));
        _predatorVelocity = new Vector2(
            ReadFiniteFloat(world, entity, "predator.velocityX"),
            ReadFiniteFloat(world, entity, "predator.velocityY"));
        var energy = ReadUnitFloat(world, entity, "predator.energy", allowZero: true);
        _predatorSated = energy >= _predator.SatedThreshold;
        _predatorFleeRadius = _predator.FleeRadius * HungerFactor(energy, _predator.RestSpeedFactor);
        _predatorView = new PredatorView(_predatorPosition, _predatorFleeRadius, energy, _predatorSated);
    }

    private void AdvanceRange(
        int tick,
        BoidPopulation.Columns columns,
        scoped ref PopulationAccessScope<Boid> access)
    {
        var positions = access.ReadCurrent(columns.Position);
        var velocities = access.ReadCurrent(columns.Velocity);
        var energies = access.ReadCurrent(columns.Energy);
        var speciesByBoid = access.ReadAll(columns.Species);
        var nextPositions = access.EditNext(columns.Position);
        var nextVelocities = access.EditNext(columns.Velocity);
        var nextEnergies = access.EditNext(columns.Energy);
        var range = access.Range;
        for (var offset = 0; offset < range.Length; offset++)
        {
            AdvanceBoid(range.Start + offset, tick, positions, velocities, energies, speciesByBoid,
                nextPositions, nextVelocities, nextEnergies, offset);
        }
    }

    private void AdvanceBoid(
        int boid,
        int tick,
        ReadOnlySpan<Vector2> positions,
        ReadOnlySpan<Vector2> velocities,
        ReadOnlySpan<float> energies,
        ReadOnlySpan<int> speciesByBoid,
        Span<Vector2> nextPositions,
        Span<Vector2> nextVelocities,
        Span<float> nextEnergies,
        int outputOffset)
    {
        var position = positions[boid];
        var velocity = velocities[boid];
        var energy = energies[boid];
        var hungry = WantsFood(energy);
        var maximumSpeed = _maximumSpeed * EnergySpeedFactor(energy, _starvedSpeedFactor);
        var acceleration = Vector2.Zero;
        var neighbors = 0;
        var candidates = 0;
        var neighborRadiusSquared = _neighborRadius * _neighborRadius;
        var collisionRadiusSquared = _collisionRadius * _collisionRadius;
        var firstCell = (boid + tick) % 9;
        var sourceSpecies = speciesByBoid[boid];
        var candidateRows = _spatialGrid.QueryCellNeighborhoodCandidates(boid, firstCell);
        while (candidates < MaximumCandidatesPerBoid
            && neighbors < MaximumNeighborsPerBoid && candidateRows.MoveNext())
        {
            var other = candidateRows.Current;
            candidates++;
            if (other != boid)
            {
                var displacement = ShortestWrappedDisplacement(position, positions[other]);
                var distanceSquared = displacement.LengthSquared();
                if (distanceSquared > 0.0f && distanceSquared < neighborRadiusSquared)
                {
                    neighbors++;

                    var distance = MathF.Sqrt(distanceSquared);
                    var direction = displacement / distance;
                    var falloff = 1.0f - distance / _neighborRadius;
                    var targetSpecies = speciesByBoid[other];
                    var pairForce = _speciesForceMatrix[sourceSpecies * _species.Length + targetSpecies];
                    acceleration += direction * (pairForce * falloff);

                    if (distanceSquared < collisionRadiusSquared)
                    {
                        var collisionFalloff = 1.0f - distance / _collisionRadius;
                        acceleration -= direction * (_collisionStrength * collisionFalloff);
                    }
                }
            }
        }

        var nearestFoodPatch = -1;
        if (hungry)
        {
            nearestFoodPatch = FindNearestStockedFoodPatch(position, out var foodDisplacement);
            if (nearestFoodPatch >= 0)
            {
                acceleration += Steer(foodDisplacement, velocity, maximumSpeed) * FoodSteeringWeight;
            }
        }

        var predatorDisplacement = ShortestWrappedDisplacement(_predatorPosition, position);
        var predatorDistanceSquared = predatorDisplacement.LengthSquared();
        var fleeRadiusSquared = _predatorFleeRadius * _predatorFleeRadius;
        var fleeWeight = _speciesFleeWeights[sourceSpecies];
        if (fleeWeight > 0.0f && predatorDistanceSquared < fleeRadiusSquared)
        {
            var fleeStrength = 1.0f - predatorDistanceSquared / fleeRadiusSquared;
            acceleration += Steer(predatorDisplacement, velocity, maximumSpeed) * (fleeWeight * fleeStrength);
        }

        velocity += Limit(acceleration, _maximumForce * 3.0f) * _deltaTime;
        velocity *= MathF.Max(0.0f, 1.0f - _friction * _deltaTime);
        var speed = velocity.Length();
        if (speed > maximumSpeed)
        {
            velocity *= maximumSpeed / speed;
        }

        position += velocity * _deltaTime;
        position.X = WrapPosition(position.X, _worldWidth);
        position.Y = WrapPosition(position.Y, _worldHeight);
        nextPositions[outputOffset] = position;
        nextVelocities[outputOffset] = velocity;

        if (!_predatorSated && predatorDistanceSquared <= _predatorConsumeRadiusSquared)
        {
            energy -= _predator.BoidEnergyDrainPerSecond * _deltaTime;
        }

        energy = Math.Max(0.0f, energy - _energyDrainPerSecond * _deltaTime);
        nextEnergies[outputOffset] = energy;
        _feedingPatchByBoid[boid] = -1;
        if (hungry && nearestFoodPatch >= 0)
        {
            var patch = _foodPatches[nearestFoodPatch];
            var displacement = ShortestWrappedDisplacement(position, patch.Position);
            if (displacement.LengthSquared() <= patch.FeedRadius * patch.FeedRadius)
            {
                _feedingPatchByBoid[boid] = nearestFoodPatch;
            }
        }
    }

    private int FindNearestStockedFoodPatch(Vector2 position, out Vector2 displacement)
    {
        var nearestIndex = -1;
        var nearestDistanceSquared = float.PositiveInfinity;
        displacement = Vector2.Zero;

        for (var index = 0; index < _foodPatches.Length; index++)
        {
            if (_foodStocks[index] <= 0.0f)
            {
                continue;
            }

            var candidate = ShortestWrappedDisplacement(position, _foodPatches[index].Position);
            var distanceSquared = candidate.LengthSquared();
            if (distanceSquared < nearestDistanceSquared)
            {
                nearestIndex = index;
                nearestDistanceSquared = distanceSquared;
                displacement = candidate;
            }
        }

        return nearestIndex;
    }

    private void AggregateFeeding(
        ReadOnlyWorldView world,
        SystemicSystemContext context,
        Span<Vector2> nextPositions,
        Span<Vector2> nextVelocities,
        Span<float> nextEnergies,
        ReadOnlySpan<int> speciesByBoid)
    {
        Array.Clear(_foodRequestCounts);
        var hungry = 0;
        for (var boid = 0; boid < _population; boid++)
        {
            if (WantsFood(nextEnergies[boid]))
            {
                hungry++;
            }

            var patchIndex = _feedingPatchByBoid[boid];
            if (patchIndex < 0 || _foodStocks[patchIndex] <= 0.0f)
            {
                continue;
            }

            _foodRequestCounts[patchIndex]++;
        }

        _hungryFraction = (float)hungry / _population;

        for (var index = 0; index < _foodPatches.Length; index++)
        {
            var requests = _foodRequestCounts[index];
            if (requests == 0)
            {
                _foodFeedShares[index] = 0.0f;
                continue;
            }

            var patch = _foodPatches[index];
            var share = Math.Min(patch.FeedAmount, _foodStocks[index] / requests);
            _foodFeedShares[index] = share;
            _foodStocks[index] = Math.Max(0.0f, _foodStocks[index] - share * requests);
        }

        for (var boid = 0; boid < _population; boid++)
        {
            var patchIndex = _feedingPatchByBoid[boid];
            if (patchIndex >= 0)
            {
                nextEnergies[boid] = Math.Min(
                    MaximumEnergy, nextEnergies[boid] + _foodFeedShares[patchIndex]);
            }
        }

        for (var index = 0; index < _foodPatches.Length; index++)
        {
            var entityId = _foodPatches[index].EntityId;
            if (world.ContainsEntity(entityId))
            {
                SetProperty(context, entityId, "food.stock", _foodStocks[index]);
            }

            _foodViews[index] = new FoodPatchView(
                _foodPatches[index].Position, _foodStocks[index], _foodPatches[index].Capacity);
        }

    }

    private Vector2 Steer(Vector2 direction, Vector2 velocity, float maximumSpeed)
    {
        var desired = NormalizeOrZero(direction) * maximumSpeed;
        return Limit(desired - velocity, _maximumForce);
    }

    private Vector2 ShortestWrappedDisplacement(Vector2 from, Vector2 to)
    {
        var displacement = to - from;
        if (displacement.X > _worldWidth * 0.5f)
        {
            displacement.X -= _worldWidth;
        }
        else if (displacement.X < -_worldWidth * 0.5f)
        {
            displacement.X += _worldWidth;
        }

        if (displacement.Y > _worldHeight * 0.5f)
        {
            displacement.Y -= _worldHeight;
        }
        else if (displacement.Y < -_worldHeight * 0.5f)
        {
            displacement.Y += _worldHeight;
        }

        return displacement;
    }

    private static float WrapPosition(float position, float size)
    {
        position %= size;
        return position < 0.0f ? position + size : position;
    }

    private static Vector2 NormalizeOrZero(Vector2 value)
    {
        var lengthSquared = value.LengthSquared();
        return lengthSquared > 0.0001f ? value / MathF.Sqrt(lengthSquared) : Vector2.Zero;
    }

    private static Vector2 Limit(Vector2 value, float maximum)
    {
        var lengthSquared = value.LengthSquared();
        return lengthSquared > maximum * maximum
            ? value * (maximum / MathF.Sqrt(lengthSquared))
            : value;
    }

    private readonly record struct FoodPatch(
        int EntityId,
        Vector2 Position,
        float Capacity,
        float RegenerationPerSecond,
        float FeedAmount,
        float FeedRadius);

    private readonly record struct Predator(
        int EntityId,
        float MaximumSpeed,
        float MaximumForce,
        float FleeRadius,
        float ConsumePerSecond,
        float ConsumeRadius,
        float EnergyDrainPerSecond,
        float EnergyPerStock,
        float SatedThreshold,
        float RestSpeedFactor,
        float BoidEnergyDrainPerSecond);

    private sealed record ParticleSpecies(
        int Index,
        int Red,
        int Green,
        int Blue,
        float FleeWeight,
        float[] Forces);
}

public readonly record struct FoodPatchView(Vector2 Position, float Stock, float Capacity);

public readonly record struct PredatorView(Vector2 Position, float FleeRadius, float Energy, bool Sated);

public readonly record struct ParticleSpeciesView(int Index, int Red, int Green, int Blue);
