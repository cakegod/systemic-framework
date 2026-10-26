using Systemic.Kernel;
using Systemic.Content;

namespace Systemic.Tests.Content;

public sealed class EntityDefinitionTests
{
    [Fact]
    public void File_authored_entities_materialize_properties_links_and_spatial_state()
    {
        var pack = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));

        var authored = pack.CreateWorld(seed: 9, valueScale: 1);
        var minerId = authored.EntityIds["quarry.ember-source"];
        var fuelId = authored.EntityIds["quarry.fuel"];
        var miner = authored.World.ById(minerId)!;
        var fuel = authored.World.ById(fuelId)!;
        var nearby = new List<int>();
        authored.World.QuerySpatialNearby(new Point2(18, 10), 10, nearby);

        Assert.Equal(fuelId, miner.Links["target"]);
        Assert.True(fuel.TryGetProperty(authored.World, "heat", out var heat));
        Assert.Equal(0, heat);
        Assert.Contains(minerId, nearby);
        Assert.Contains(fuelId, nearby);
    }
}
