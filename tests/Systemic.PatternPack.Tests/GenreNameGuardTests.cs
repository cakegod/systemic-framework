using Systemic.Host;
using Xunit;

namespace Systemic.Tests.Patterns;

public sealed class GenreNameGuardTests
{
    private static readonly string[] s_forbidden =
        ["Talent", "Merge", "Prestige", "Upgrade", "Producer", "Colony"];

    [Fact]
    public void NoFrameworkTypeUsesAGenreName()
    {
        var framework = new[] { typeof(SystemicGame).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsPublic || type.IsNestedPublic)
            .ToArray();

        var offenders = framework
            .Where(type => s_forbidden.Any(name =>
                type.Name.Equals(name, StringComparison.Ordinal)
                || type.Name.Equals(name + "s", StringComparison.Ordinal)))
            .Select(type => type.FullName)
            .ToArray();

        Assert.Empty(offenders);
    }
}
