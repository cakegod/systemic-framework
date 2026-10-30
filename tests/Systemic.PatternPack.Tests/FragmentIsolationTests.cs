using System.Text.Json;
using System.Text.Json.Nodes;
using Systemic.Content;
using Xunit;

namespace Systemic.Tests.Patterns;

/// <summary>
/// A fragment is only copyable if it carries the wiring its rules need. Each one is loaded here on
/// its own, with the pack's shared facts and entities, so a schedule or bundle left behind at file
/// level fails instead of producing rules that silently never fire.
/// </summary>
public sealed class FragmentIsolationTests
{
    private static string SampleRoot => Path.Combine(AppContext.BaseDirectory, "samples", "pattern-pack");

    [Fact]
    public void EveryFragmentLoadsAloneWithItsOwnSchedulesAndBundles()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(SampleRoot, "rules.json")));
        var fragments = document.RootElement.GetProperty("fragments"u8).EnumerateArray().ToArray();
        var declaredFacts = document.RootElement.GetProperty("facts"u8).EnumerateArray()
            .Select(fact => fact.GetProperty("id"u8).GetString()!)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(8, fragments.Length);
        foreach (var fragment in fragments)
        {
            var id = fragment.GetProperty("id"u8).GetString()!;
            using var pack = FragmentPack.Create(fragment);

            var content = ContentPackLoader.Load(pack.Root);

            Assert.NotEmpty(content.Rules.Rules);
            Assert.All(content.Rules.Rules, rule => Assert.StartsWith(id, rule.Id, StringComparison.Ordinal));
            // Facts are pack-level vocabulary: every event a fragment listens to is declared in the
            // file it is copied into, not carried by the fragment itself.
            Assert.All(content.Rules.Rules, rule => Assert.Contains(rule.EventType, declaredFacts));
            Assert.Equal(id == "pattern.producer" ? 1 : 0, content.Rules.Schedules.Length);
            Assert.Equal(id == "pattern.talent" ? 1 : 0, content.Rules.Bundles.Count);
        }
    }

    private sealed class FragmentPack : IDisposable
    {
        private FragmentPack(string root) => Root = root;

        public string Root { get; }

        public static FragmentPack Create(JsonElement fragment)
        {
            var root = Directory.CreateTempSubdirectory("systemic-fragment-").FullName;
            try
            {
                File.Copy(Path.Combine(SampleRoot, "game.json"), Path.Combine(root, "game.json"));
                File.Copy(Path.Combine(SampleRoot, "entities.json"), Path.Combine(root, "entities.json"));
                var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(SampleRoot, "rules.json")))!;
                var id = fragment.GetProperty("id"u8).GetString()!;
                var rules = new JsonObject
                {
                    ["schemaVersion"] = 2,
                    ["rules"] = new JsonArray(),
                    ["facts"] = sample["facts"]!.DeepClone(),
                    ["fragments"] = new JsonArray(JsonNode.Parse(fragment.GetRawText())),
                    ["useFragments"] = new JsonArray(id),
                };
                File.WriteAllText(Path.Combine(root, "rules.json"), rules.ToJsonString());
                return new FragmentPack(root);
            }
            catch
            {
                Directory.Delete(root, recursive: true);
                throw;
            }
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
