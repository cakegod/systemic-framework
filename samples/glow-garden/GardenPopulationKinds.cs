using System.Numerics;
using Systemic.Kernel;

namespace GlowGarden;

[PopulationSchema("garden.plants", 1)]
internal partial struct GardenPlant
{
    [PopulationColumn("id")]
    public int StableId;

    [PopulationColumn("position")]
    public Vector2 Position;

    [PopulationColumn("growth")]
    public float Growth;
}

[PopulationSchema("garden.creatures", 1)]
internal partial struct GardenCreature
{
    [PopulationColumn("id")]
    public int StableId;

    [PopulationColumn("kind")]
    public int Kind;

    [PopulationColumn("position")]
    public Vector2 Position;

    [PopulationColumn("carriedGlow")]
    public int CarriedGlow;

    [PopulationColumn("cooldown")]
    public int Cooldown;

    [PopulationColumn("isCopy")]
    public bool IsCopy;

    [PopulationColumn("leapProgress")]
    public float LeapProgress;

    [PopulationColumn("copyExpiryTick")]
    public int CopyExpiryTick;
}

[PopulationSchema("garden.motes", 1)]
internal partial struct GardenMote
{
    [PopulationColumn("id")]
    public int StableId;

    [PopulationColumn("position")]
    public Vector2 Position;

    [PopulationColumn("velocity")]
    public Vector2 Velocity;

    [PopulationColumn("value")]
    public int Value;
}
