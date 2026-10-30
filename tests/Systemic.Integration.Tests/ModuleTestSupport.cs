using System.Collections.Immutable;
using Systemic.Content;
using Systemic.Gameplay2D;

namespace Systemic.Tests;

internal static class SystemicTestContent
{
    public static ContentPack Load(string root) => ContentPackLoader.Load(root,
        new ContentModuleRegistry()
            .Add(new IncrementalContentModule())
            .Add(new PresentationContentModule())
            .Add(new Gameplay2DContentModule()));

    public static IncrementalModuleContent Incremental(ContentPack pack) =>
        pack.Modules.Select(module => module.Content).OfType<IncrementalModuleContent>().Single();

    public static PresentationModuleContent Presentation(ContentPack pack) =>
        pack.Modules.Select(module => module.Content).OfType<PresentationModuleContent>().Single();

    public static ContentPack WithPresentation(ContentPack pack, PresentationModuleContent replacement)
    {
        var modules = pack.Modules.Select(module => module.Content is PresentationModuleContent
            ? module with { Content = replacement }
            : module).ToImmutableArray();
        return pack with { Modules = modules };
    }
}
