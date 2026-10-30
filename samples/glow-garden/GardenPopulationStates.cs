using System.Collections;
using Systemic.Host;
using Systemic.Kernel;

namespace GlowGarden;

/// <summary>A live projection over Kernel columns; it owns no parallel state or row cache.</summary>
internal abstract class PopulationStates<TKind, TState> : IReadOnlyList<TState>
    where TKind : IPopulationKind
{
    protected PopulationStates(Population<TKind> population, int capacity)
    {
        Population = population;
        Capacity = capacity;
        population.Reserve(capacity);
    }

    internal Population<TKind> Population { get; }
    private SystemicSystemContext? _context;
    private int Capacity { get; }
    public int Count => Population.Count;
    public abstract TState this[int index] { get; set; }

    internal void BindContext(SystemicSystemContext? context) => _context = context;

    internal PopulationLease<TKind> BorrowLease() => _context is { } context
        ? context.BorrowPopulation(Population)
        : Population.Borrow();

    internal void WithAccess<TCallbackState>(
        PopulationRange range,
        TCallbackState state,
        PopulationAccessAction<TKind, TCallbackState> callback)
    {
        using var lease = BorrowLease();
        lease.WithAccess(range, state, callback);
    }

    internal void Add(TState value)
    {
        if (Count >= Capacity)
        {
            throw new InvalidOperationException("Garden population cap reached.");
        }

        this[Population.AddRange(1).Start] = value;
    }

    internal void RemoveAt(int index) => Population.RemoveSwapBack(index);
    internal void Clear()
    {
        while (Count > 0)
        {
            RemoveAt(Count - 1);
        }
    }

    public IEnumerator<TState> GetEnumerator()
    {
        for (var row = 0; row < Count; row++)
        {
            yield return this[row];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class PlantStates : PopulationStates<GardenPlant, PlantState>
{
    internal PlantStates(World world) : base(GardenPlantPopulation.Define(world), GardenSimulation.MaxPlants)
    {
        Columns = GardenPlantPopulation.Bind(Population);
    }

    internal GardenPlantPopulation.Columns Columns { get; }
    public override PlantState this[int index]
    {
        get
        {
            var state = new PlantRowState(Columns);
            WithAccess(new PopulationRange(index, 1), state,
                static (PlantRowState callbackState, scoped ref PopulationAccessScope<GardenPlant> access) =>
                {
                    var row = callbackState.Columns.ReadRow(ref access, 0);
                    callbackState.Value = new(row.StableId, row.Position, row.Growth);
                });
            return state.Value;
        }
        set
        {
            var state = new PlantWriteState(Columns, value);
            WithAccess(new PopulationRange(index, 1), state,
                static (PlantWriteState callbackState, scoped ref PopulationAccessScope<GardenPlant> access) =>
                    callbackState.Columns.WriteRow(ref access, 0, new GardenPlantPopulation.Row
                    {
                        StableId = callbackState.Value.Id,
                        Position = callbackState.Value.Position,
                        Growth = callbackState.Value.Growth
                    }));
        }
    }

    private sealed class PlantRowState(GardenPlantPopulation.Columns columns)
    {
        public GardenPlantPopulation.Columns Columns { get; } = columns;
        public PlantState Value { get; set; }
    }

    private sealed class PlantWriteState(GardenPlantPopulation.Columns columns, PlantState value)
    {
        public GardenPlantPopulation.Columns Columns { get; } = columns;
        public PlantState Value { get; } = value;
    }
}

internal sealed class CreatureStates : PopulationStates<GardenCreature, CreatureState>
{
    internal CreatureStates(World world) : base(GardenCreaturePopulation.Define(world), GardenSimulation.MaxWorkers + GardenSimulation.MaxCopies)
    {
        Columns = GardenCreaturePopulation.Bind(Population);
    }

    internal GardenCreaturePopulation.Columns Columns { get; }
    public override CreatureState this[int index]
    {
        get
        {
            var state = new CreatureRowState(Columns);
            WithAccess(new PopulationRange(index, 1), state,
                static (CreatureRowState callbackState, scoped ref PopulationAccessScope<GardenCreature> access) =>
                {
                    var row = callbackState.Columns.ReadRow(ref access, 0);
                    callbackState.Value = new(
                        row.StableId,
                        (CreatureKind)row.Kind,
                        row.Position,
                        row.CarriedGlow,
                        row.Cooldown,
                        row.IsCopy,
                        row.LeapProgress,
                        row.Heading);
                });
            return state.Value;
        }
        set
        {
            var state = new CreatureWriteState(Columns, value);
            WithAccess(new PopulationRange(index, 1), state,
                static (CreatureWriteState callbackState, scoped ref PopulationAccessScope<GardenCreature> access) =>
                {
                    var row = callbackState.Columns.ReadRow(ref access, 0);
                    row.StableId = callbackState.Value.Id;
                    row.Kind = (int)callbackState.Value.Kind;
                    row.Position = callbackState.Value.Position;
                    row.CarriedGlow = callbackState.Value.CarriedGlow;
                    row.Cooldown = callbackState.Value.Cooldown;
                    row.IsCopy = callbackState.Value.IsCopy;
                    row.LeapProgress = callbackState.Value.LeapProgress;
                    row.Heading = callbackState.Value.Heading;
                    callbackState.Columns.WriteRow(ref access, 0, row);
                });
        }
    }

    private sealed class CreatureRowState(GardenCreaturePopulation.Columns columns)
    {
        public GardenCreaturePopulation.Columns Columns { get; } = columns;
        public CreatureState Value { get; set; }
    }

    private sealed class CreatureWriteState(GardenCreaturePopulation.Columns columns, CreatureState value)
    {
        public GardenCreaturePopulation.Columns Columns { get; } = columns;
        public CreatureState Value { get; } = value;
    }
}

internal sealed class MoteStates : PopulationStates<GardenMote, MoteState>
{
    internal MoteStates(World world) : base(GardenMotePopulation.Define(world), GardenSimulation.MaxMotes)
    {
        Columns = GardenMotePopulation.Bind(Population);
    }

    internal GardenMotePopulation.Columns Columns { get; }
    public override MoteState this[int index]
    {
        get
        {
            var state = new MoteRowState(Columns);
            WithAccess(new PopulationRange(index, 1), state,
                static (MoteRowState callbackState, scoped ref PopulationAccessScope<GardenMote> access) =>
                {
                    var row = callbackState.Columns.ReadRow(ref access, 0);
                    callbackState.Value = new(row.StableId, row.Position, row.Velocity, row.Value);
                });
            return state.Value;
        }
        set
        {
            var state = new MoteWriteState(Columns, value);
            WithAccess(new PopulationRange(index, 1), state,
                static (MoteWriteState callbackState, scoped ref PopulationAccessScope<GardenMote> access) =>
                    callbackState.Columns.WriteRow(ref access, 0, new GardenMotePopulation.Row
                    {
                        StableId = callbackState.Value.Id,
                        Position = callbackState.Value.Position,
                        Velocity = callbackState.Value.Velocity,
                        Value = callbackState.Value.Value
                    }));
        }
    }

    private sealed class MoteRowState(GardenMotePopulation.Columns columns)
    {
        public GardenMotePopulation.Columns Columns { get; } = columns;
        public MoteState Value { get; set; }
    }

    private sealed class MoteWriteState(GardenMotePopulation.Columns columns, MoteState value)
    {
        public GardenMotePopulation.Columns Columns { get; } = columns;
        public MoteState Value { get; } = value;
    }
}
