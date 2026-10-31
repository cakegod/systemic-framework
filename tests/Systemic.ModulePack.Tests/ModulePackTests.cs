using ModulePack;
using Systemic.Content;
using Systemic.Host;
using Systemic.Save;
using Xunit;

namespace Systemic.Tests.Modules;

public sealed class ModulePackTests
{
    private static ContentPack Pack() =>
        ContentPackLoader.Load(Path.Combine(AppContext.BaseDirectory, "samples", "module-pack"));

    [Fact]
    public void AModuleComposesSystemsCommandsAndEventsOverADataOnlyPack()
    {
        var content = Pack();
        var pulses = new PulseCounter();
        using var host = HeadlessSystemicHost.Create(content, seed: 12, configure: builder => builder
            .AddModule(new ForgeModule())
            .AddEvent<ForgePulsed>(pulses));
        var forge = host.Game.EntityId("forge");

        host.RunTicks(3);
        var accepted = host.Game.Execute(new StokeCommand(forge, 4));

        Assert.False(host.Game.SupportsRules);
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "samples", "module-pack", "rules.json")));
        Assert.True(accepted.Accepted);
        Assert.Equal(4, host.Game.World.EffectiveProperty(host.Game.World.ById(forge)!, "heat"));
        Assert.Equal(3, pulses.Count);
        Assert.Equal(1, host.Game.ModuleVersions["sample.module-pack.forge"]);
    }

    [Fact]
    public void TheModuleRestoresItsOwnSaveAndAChangedVersionIsRejected()
    {
        var content = Pack();
        string save;
        using (var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 4, valueScale: 1))
            .AddModule(new ForgeModule())
            .Build())
        {
            save = new SystemicSaveCodec().Save(game);
        }

        var restored = new SystemicSaveCodec().Load(content, save);
        using var reloaded = new SystemicGameBuilder(content, restored)
            .AddModule(new ForgeModule())
            .Build();
        Assert.Equal(1, reloaded.ModuleVersions["sample.module-pack.forge"]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            new SystemicGameBuilder(content, new SystemicSaveCodec().Load(content, save))
                .AddModule(new ForgeModule(version: 2)));

        Assert.Contains("version 1", error.Message, StringComparison.Ordinal);
        Assert.Contains("version 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASaveThatComposedTheModuleWillNotRestoreWithoutIt()
    {
        var content = Pack();
        string save;
        using (var game = new SystemicGameBuilder(content, content.CreateWorld(seed: 4, valueScale: 1))
            .AddModule(new ForgeModule())
            .Build())
        {
            save = new SystemicSaveCodec().Save(game);
        }

        var restored = new SystemicSaveCodec().Load(content, save);
        var builder = new SystemicGameBuilder(content, restored);

        var error = Assert.Throws<InvalidOperationException>(() => builder.Build());

        Assert.Contains("sample.module-pack.forge", error.Message, StringComparison.Ordinal);
    }
}
