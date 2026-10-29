using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Systemic.Kernel.Generator;

/// <summary>Provides assembly-local markers for the strict determinism analyzer.</summary>
[Generator]
public sealed class DeterminismMarkerGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterSourceOutput(context.CompilationProvider, static (output, compilation) =>
        {
            var strict = NeedsMarker(compilation, "Systemic.Kernel.StrictSimulationAttribute");
            var cosmetic = NeedsMarker(compilation, "Systemic.Kernel.CosmeticOnlyAttribute");
            if (!strict && !cosmetic) return;

            var source = new StringBuilder("#nullable enable\nnamespace Systemic.Kernel;\n");
            if (strict) AppendMarker(source, "StrictSimulationAttribute", "Marks simulation code for strict determinism diagnostics.");
            if (cosmetic) AppendMarker(source, "CosmeticOnlyAttribute", "Excludes cosmetic code from strict determinism diagnostics.");
            output.AddSource("Systemic.DeterminismMarkers.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
        });
    }

    private static bool NeedsMarker(Compilation compilation, string metadataName)
    {
        var existing = compilation.GetTypeByMetadataName(metadataName);
        return existing is null || !compilation.IsSymbolAccessibleWithin(existing, compilation.Assembly);
    }

    private static void AppendMarker(StringBuilder source, string name, string summary)
    {
        source.AppendLine($"/// <summary>{summary}</summary>");
        source.AppendLine("[global::System.AttributeUsage(global::System.AttributeTargets.Assembly | global::System.AttributeTargets.Class | global::System.AttributeTargets.Struct | global::System.AttributeTargets.Method | global::System.AttributeTargets.Property | global::System.AttributeTargets.Field, Inherited = false)]");
        source.AppendLine($"internal sealed class {name} : global::System.Attribute {{ }}");
    }
}
