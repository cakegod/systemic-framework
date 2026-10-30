using Systemic.Host;

namespace Systemic.Tests.Host;

public sealed class CoreAssemblyBoundaryTests
{
    [Fact]
    public void CoreAssemblyHasNoOptionalModuleReferences()
    {
        var core = typeof(SystemicGame).Assembly;

        Assert.DoesNotContain(core.GetReferencedAssemblies(), assembly =>
            assembly.Name is "Systemic.Incremental"
                or "Systemic.Presentation"
                or "Systemic.Gameplay2D");
        Assert.DoesNotContain(core.GetTypes(), type =>
            type.Namespace?.StartsWith("Systemic.Incremental", StringComparison.Ordinal) == true
                || type.Namespace?.StartsWith("Systemic.Presentation", StringComparison.Ordinal) == true
                || type.Namespace?.StartsWith("Systemic.Preview", StringComparison.Ordinal) == true
                || type.Namespace?.StartsWith("Systemic.Gameplay2D", StringComparison.Ordinal) == true);
    }
}
