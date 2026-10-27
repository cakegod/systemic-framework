using Systemic.Kernel;

namespace PixelMine;

[PopulationSchema("sample.pixel-mine.balls", 1)]
internal partial struct MineBall
{
    [PopulationColumn("x", DoubleBuffered = true)]
    public int X;

    [PopulationColumn("y", DoubleBuffered = true)]
    public int Y;

    [PopulationColumn("directionX", DoubleBuffered = true)]
    public int DirectionX;

    [PopulationColumn("directionY", DoubleBuffered = true)]
    public int DirectionY;

    [PopulationColumn("kind", DoubleBuffered = true)]
    public int Kind;
}
