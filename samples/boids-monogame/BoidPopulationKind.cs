using System.Numerics;
using Systemic.Kernel;

namespace Boids.MonoGame;

[PopulationSchema("sample.boids", 1)]
internal partial struct Boid
{
    [PopulationColumn("position", DoubleBuffered = true)]
    public Vector2 Position;

    [PopulationColumn("velocity", DoubleBuffered = true)]
    public Vector2 Velocity;

    [PopulationColumn("energy", DoubleBuffered = true)]
    public float Energy;

    [PopulationColumn("species")]
    public int Species;
}
