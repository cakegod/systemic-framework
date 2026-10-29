using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Save;
using Systemic.Tests.Content;
using VerifyXunit;

namespace Systemic.Tests.Save;

public sealed class SaveSnapshotTests
{
    [Fact]
    public Task Save_document_matches_the_checked_in_snapshot()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var world = content.CreateWorld(seed: 9, valueScale: 1).World;
        world.SetSpatialBounds(world.ById(1)!, new Bounds2(2, 3, 4, 5));
        world.SetModifier("test.foundation", world.ById(1)!, "health", ModifierOperation.Add,
            value: 2, remainingTicks: 3, priority: 4, stage: ModifierStage.Foundation);
        using var game = new SystemicGameBuilder(content, world).Build();

        return Verifier.Verify(new SystemicSaveCodec().Save(game), extension: "json");
    }
}
