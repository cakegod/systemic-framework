using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Host;

public sealed class SaveOnlyStateTests
{
    [Fact]
    public void Save_only_extension_is_not_snapshotted_each_tick()
    {
        var extension = new SaveOnlyExtension();
        using var game = new SystemicGameBuilder(EmptyContent(), new World(seed: 1, valueScale: 1))
            .RegisterStatefulExtension(extension)
            .Build();

        game.Step();

        Assert.Equal(0, extension.Captures);
    }

    [Fact]
    public void Ordinary_stateful_extension_is_still_snapshotted_each_tick()
    {
        var extension = new SnapshotExtension();
        using var game = new SystemicGameBuilder(EmptyContent(), new World(seed: 1, valueScale: 1))
            .RegisterStatefulExtension(extension)
            .Build();

        game.Step();

        Assert.Equal(1, extension.Captures);
    }

    private static ContentPack EmptyContent() => new("", new GameManifest("test", "Test", 30, 4),
        new CompiledRuleSet([]), new CompiledEntities([]));

    private class CountingExtension
    {
        public int Captures { get; private set; }

        public JsonElement CaptureState()
        {
            Captures++;
            return JsonSerializer.SerializeToElement(0);
        }

        public void RestoreState(JsonElement state)
        {
        }
    }

    private sealed class SaveOnlyExtension : CountingExtension, ISaveOnlyStateExtension
    {
        public string Id => "test.save-only";
    }

    private sealed class SnapshotExtension : CountingExtension, ISystemicStatefulExtension
    {
        public string Id => "test.snapshot";
    }
}
