using Systemic.Content;
using Systemic.Host;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Tests.Host;

/// <summary>
/// A pack loaded from disk carries no action handlers, because handlers are code. The builder has to
/// merge them into the compiled rule set before the engine binds, or a rule that invokes an action
/// silently does nothing in every game that loads content from a directory.
/// </summary>
public sealed class RuleActionRegistrationTests
{
    [Fact]
    public void ALoadedPackRunsAnActionTheBuilderRegistered()
    {
        using var pack = new ActionPack();
        var content = ContentPackLoader.Load(pack.Root);
        var world = new World(seed: 4, valueScale: 1);
        var target = world.Add(new DataEntity(["node"], [], ("struck", 0.0)));
        var invocations = 0;
        using var game = new SystemicGameBuilder(content, world)
            .AddRuleAction("strike", (in RuleActionContext context) =>
            {
                invocations++;
                context.Target!.TrySetProperty(context.World, "struck", 1);
            })
            .AddSystem(new StrikeSystem(target.Id))
            .Build();

        game.Step();

        Assert.Equal(1, invocations);
        Assert.True(target.TryGetProperty(world, "struck", out var struck));
        Assert.Equal(1, struck);
    }

    [Fact]
    public void RegisteringTheSameActionTwiceFailsLoudly()
    {
        using var pack = new ActionPack();
        var content = ContentPackLoader.Load(pack.Root);
        var builder = new SystemicGameBuilder(content, new World(seed: 5, valueScale: 1))
            .AddRuleAction("strike", (in RuleActionContext context) => { });

        var error = Assert.Throws<InvalidOperationException>(() =>
            builder.AddRuleAction("strike", (in RuleActionContext context) => { }));

        Assert.Contains("strike", error.Message, StringComparison.Ordinal);
    }

    private sealed class StrikeSystem(int targetId) : ISystemicSystem
    {
        public string Id => "test.strike";

        public int Order => 0;

        public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];

        public void Step(SystemicSystemContext context) =>
            context.Publish(new SimulationFact("strike", 0, targetId));
    }

    private sealed class ActionPack : IDisposable
    {
        public ActionPack() => Write();

        public string Root { get; } = Directory.CreateTempSubdirectory("systemic-actions-").FullName;

        public void Dispose() => Directory.Delete(Root, recursive: true);

        private void Write()
        {
            File.WriteAllText(Path.Combine(Root, "game.json"), """
                {
                  "schemaVersion": 3,
                  "id": "sample.actions",
                  "name": "Actions",
                  "tickRateHz": 30,
                  "maxCatchUpSteps": 4,
                  "rules": "rules.json",
                  "entities": "entities.json"
                }
                """);
            File.WriteAllText(Path.Combine(Root, "entities.json"), """
                {
                  "schemaVersion": 2,
                  "entities": [
                    { "id": "sample.node", "kind": "product", "tags": ["node"], "properties": { "struck": 0 } }
                  ]
                }
                """);
            File.WriteAllText(Path.Combine(Root, "rules.json"), """
                {
                  "schemaVersion": 2,
                  "rules": [
                    {
                      "id": "test.strike",
                      "on": "strike",
                      "priority": 0,
                      "effects": [
                        { "kind": "action", "subject": "target", "action": "strike" }
                      ]
                    }
                  ]
                }
                """);
        }
    }
}
