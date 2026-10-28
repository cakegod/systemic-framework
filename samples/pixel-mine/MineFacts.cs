using Systemic.Rules;

namespace PixelMine;

/// <summary>Fact construction lives outside the system so the mine never handles the fact's floating-point amount.</summary>
internal static class MineFacts
{
    public static SimulationFact GoldenPixelFound(int mineId) =>
        new(new FactTypeId(MineContent.GoldenFound), mineId, mineId);
}
