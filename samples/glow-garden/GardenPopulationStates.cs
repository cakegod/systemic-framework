using System.Collections;
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
    private int Capacity { get; }
    public int Count => Population.Count;
    public abstract TState this[int index] { get; set; }

    internal void Add(TState value)
    {
        if (Count >= Capacity) throw new InvalidOperationException("Garden population cap reached.");
        this[Population.AddRange(1).Start] = value;
    }

    internal void RemoveAt(int index) => Population.RemoveSwapBack(index);
    internal void Clear()
    {
        while (Count > 0) RemoveAt(Count - 1);
    }

    public IEnumerator<TState> GetEnumerator()
    {
        for (var row = 0; row < Count; row++) yield return this[row];
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
        get => new(
            Columns.StableId.ReadOnlySpan[index],
            Columns.Position.ReadOnlySpan[index],
            Columns.Growth.ReadOnlySpan[index]);
        set
        {
            Columns.StableId.EditRange(index, 1)[0] = value.Id;
            Columns.Position.EditRange(index, 1)[0] = value.Position;
            Columns.Growth.EditRange(index, 1)[0] = value.Growth;
        }
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
        get => new(
            Columns.StableId.ReadOnlySpan[index],
            (CreatureKind)Columns.Kind.ReadOnlySpan[index],
            Columns.Position.ReadOnlySpan[index],
            Columns.CarriedGlow.ReadOnlySpan[index],
            Columns.Cooldown.ReadOnlySpan[index],
            Columns.IsCopy.ReadOnlySpan[index],
            Columns.LeapProgress.ReadOnlySpan[index],
            Columns.Heading.ReadOnlySpan[index]);
        set
        {
            Columns.StableId.EditRange(index, 1)[0] = value.Id;
            Columns.Kind.EditRange(index, 1)[0] = (int)value.Kind;
            Columns.Position.EditRange(index, 1)[0] = value.Position;
            Columns.CarriedGlow.EditRange(index, 1)[0] = value.CarriedGlow;
            Columns.Cooldown.EditRange(index, 1)[0] = value.Cooldown;
            Columns.IsCopy.EditRange(index, 1)[0] = value.IsCopy;
            Columns.LeapProgress.EditRange(index, 1)[0] = value.LeapProgress;
            Columns.Heading.EditRange(index, 1)[0] = value.Heading;
        }
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
        get => new(
            Columns.StableId.ReadOnlySpan[index],
            Columns.Position.ReadOnlySpan[index],
            Columns.Velocity.ReadOnlySpan[index],
            Columns.Value.ReadOnlySpan[index]);
        set
        {
            Columns.StableId.EditRange(index, 1)[0] = value.Id;
            Columns.Position.EditRange(index, 1)[0] = value.Position;
            Columns.Velocity.EditRange(index, 1)[0] = value.Velocity;
            Columns.Value.EditRange(index, 1)[0] = value.Value;
        }
    }
}
