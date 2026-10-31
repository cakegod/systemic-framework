using System.Reflection;
using Systemic.Host;
using Xunit;

namespace Systemic.Tests.Patterns;

public sealed class GenreNameGuardTests
{
    private static readonly string[] s_forbidden =
        ["Talent", "Merge", "Prestige", "Upgrade", "Producer", "Colony"];

    // WHY: optional modules ship in the framework, so the guard must see them. The frozen
    // Incremental module predates the rule and is deleted by checklist item 1.26; exempting it
    // keeps the guard honest for everything this phase adds without a churn-only rename.
    private static readonly string[] s_exemptAssemblies = ["Systemic.Incremental"];

    private static readonly string[] s_optionalAssemblies =
        ["Systemic.Presentation", "Systemic.Gameplay2D", "Systemic.Rendering", "Systemic.Incremental"];

    [Fact]
    public void NoFrameworkTypeUsesAGenreName()
    {
        var offenders = FrameworkAssemblies()
            .SelectMany(assembly => assembly.GetTypes())
            .Where(type => type.IsPublic || type.IsNestedPublic)
            .SelectMany(OffendingMembers)
            .ToArray();

        Assert.Empty(offenders);
    }

    private static IEnumerable<Assembly> FrameworkAssemblies()
    {
        yield return typeof(SystemicGame).Assembly;
        foreach (var name in s_optionalAssemblies)
        {
            Assembly? assembly = null;
            try
            {
                assembly = Assembly.Load(new AssemblyName(name));
            }
            catch (FileNotFoundException)
            {
            }
            catch (FileLoadException)
            {
            }

            if (assembly is not null)
            {
                yield return assembly;
            }
        }
    }

    private static IEnumerable<string> OffendingMembers(Type type)
    {
        if (s_exemptAssemblies.Contains(type.Assembly.GetName().Name, StringComparer.Ordinal))
        {
            yield break;
        }

        if (IsForbidden(type.Name))
        {
            yield return $"{type.FullName} (type)";
        }

        foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance
            | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (IsForbidden(member.Name))
            {
                yield return $"{type.FullName}.{member.Name} (member)";
            }
        }
    }

    private static bool IsForbidden(string name) =>
        s_forbidden.Any(word => name.Contains(word, StringComparison.Ordinal));
}
