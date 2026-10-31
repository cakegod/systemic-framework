using System.Text.Json;
using Systemic.Content;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Tests.Content;

public sealed class ContentModuleRegistryTests
{
    [Fact]
    public void DuplicateModuleIdsAreRejected()
    {
        var registry = new ContentModuleRegistry();
        registry.Add(new TestModule("test.module"));

        var error = Assert.Throws<InvalidOperationException>(() => registry.Add(new TestModule("test.module")));

        Assert.Contains("test.module", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ContentReferencesCanBeResolvedWithoutModuleTypes()
    {
        var references = new ContentReferenceCatalog();
        references.Add("entity", "actor.player", "entities.json");

        Assert.True(references.Contains("entity", "actor.player"));
        Assert.False(references.Contains("choice", "actor.player"));
    }

    [Fact]
    public void OptionalModuleCompilesAndRegistersServicesOnlyWhenDeclared()
    {
        using var pack = CoreOnlyPack.Create();
        var module = new TestModule("test.module");
        var registry = new ContentModuleRegistry().Add(module);

        var absentContent = ContentPackLoader.Load(pack.Root, registry);
        var absentGame = new SystemicGameBuilder(absentContent, absentContent.CreateWorld(4, 1)).Build();

        Assert.Equal(0, module.CompileCount);
        Assert.Throws<KeyNotFoundException>(() => absentGame.GetService<TestService>());

        var gamePath = Path.Combine(pack.Root, "game.json");
        File.WriteAllText(gamePath, File.ReadAllText(gamePath).Replace(
            "\"entities\": \"entities.json\"", "\"entities\": \"entities.json\",\n  \"modules\": { \"test.module\": { \"label\": \"loaded\" } }",
            StringComparison.Ordinal));
        var declaredContent = ContentPackLoader.Load(pack.Root, registry);
        var declaredGame = new SystemicGameBuilder(declaredContent, declaredContent.CreateWorld(4, 1)).Build();

        Assert.Equal(1, module.CompileCount);
        Assert.Equal("loaded", declaredGame.GetService<TestService>().Label);
    }

    [Fact]
    public void FactObserversRunInExplicitOrderBeforeRuleDispatchFinishes()
    {
        using var pack = CoreOnlyPack.Create();
        var content = ContentPackLoader.Load(pack.Root);
        var world = content.CreateWorld(4, 1);
        var observed = new List<string>();
        var game = new SystemicGameBuilder(content, world)
            .AddFactObserver(20, (_, _) => observed.Add("later"))
            .AddFactObserver(10, (_, _) => observed.Add("earlier"))
            .Build();

        game.Publish(new SimulationFact("contact", 0, 0));

        Assert.Equal(["earlier", "later"], observed);
    }

    private sealed class TestModule(string id) : ISystemicContentModule
    {
        public string Id { get; } = id;
        public int Version => 1;
        public int CompileCount { get; private set; }

        public ISystemicModuleContent Compile(ContentModuleContext context, JsonElement declaration)
        {
            CompileCount++;
            return new TestContent(declaration.GetProperty("label"u8).GetString() ?? "missing");
        }

        public void Configure(SystemicGameBuilder builder, ISystemicModuleContent content)
        {
            builder.AddService(new TestService(((TestContent)content).Label));
        }
    }

    private sealed record TestContent(string Label) : ISystemicModuleContent;

    public sealed record TestService(string Label);
}
