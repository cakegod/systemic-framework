using Systemic.Host;

namespace Systemic.Tests.Host;

public sealed class HeadlessSystemicHostTests
{
    [Fact]
    public void HeadlessHostRunsTheSameTicksUiAndSaveLifecycleAsOtherAdapters()
    {
        var content = SystemicTestContent.Load(Path.Combine(AppContext.BaseDirectory, "samples", "incremental-quarry"));
        var host = HeadlessSystemicHost.Create(content, seed: 44);
        var secondHost = HeadlessSystemicHost.Create(content, seed: 44);

        host.RunTicks(4);
        secondHost.RunTicks(4);
        var layout = host.Game.EvaluateUi("quarry.hud");
        var save = host.Save();
        var restored = HeadlessSystemicHost.Restore(content, save);
        host.Step();
        secondHost.Step();
        restored.Step();

        Assert.True(layout.Children[1].NumberValue > 0);
        Assert.Equal(host.Save(), secondHost.Save());
        Assert.Equal(host.Save(), restored.Save());
    }
}
