using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Systemic.Kernel;
using Systemic.Kernel.Generator;
using Xunit;

namespace Systemic.Kernel.Generator.Tests;

public sealed class SystemAccessAnalyzerTests
{
    [Fact]
    public void Diagnostics_are_errors_in_the_system_access_category_and_link_to_the_guide()
    {
        var analyzerType = typeof(StrictDeterminismAnalyzer).Assembly
            .GetType("Systemic.Kernel.Generator.SystemAccessAnalyzer");
        Assert.NotNull(analyzerType);
        var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType)!;
        var diagnostics = analyzer.SupportedDiagnostics;

        Assert.Equal(5, diagnostics.Length);
        foreach (var descriptor in diagnostics)
        {
            Assert.Equal("SystemAccess", descriptor.Category);
            Assert.Equal(DiagnosticSeverity.Error, descriptor.DefaultSeverity);
            Assert.True(descriptor.IsEnabledByDefault);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Description.ToString()));
            Assert.Equal(
                $"https://github.com/cakegod/systemic-framework/blob/main/docs/api/analyzers.md#{descriptor.Id.ToLowerInvariant()}",
                descriptor.HelpLinkUri);
        }
    }

    [Fact]
    public async Task Default_access_allows_immediate_world_operations()
    {
        const string source = """
            using Systemic.Host;
            internal sealed class DefaultSystem : ISystemicSystem
            {
                public string Id => "default";
                public void Step(SystemicSystemContext context)
                {
                    _ = context.GetWritableWorld();
                    context.SetProperty(null!, "value", 1);
                    context.Publish(default);
                    context.Execute(default);
                }
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Direct_write_world_access_allows_immediate_world_operations()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class CollectionExpressionSystem : ISystemicSystem
            {
                public string Id => "collection";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];
                public void Step(SystemicSystemContext context) => _ = context.GetWritableWorld();
            }
            internal sealed class ArraySystem : ISystemicSystem
            {
                public string Id => "array";
                public IReadOnlyList<SystemAccess> Access => new SystemAccess[] { SystemAccess.WriteWorld };
                public void Step(SystemicSystemContext context) => context.Publish(default);
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Missing_write_world_access_reports_each_immediate_world_operation()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class RestrictedSystem : ISystemicSystem
            {
                public string Id => "restricted";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.ReadWorld];
                public void Step(SystemicSystemContext context)
                {
                    _ = context.GetWritableWorld();
                    context.SetProperty(null!, "value", 1);
                    context.Publish(default);
                    context.Execute(default);
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Equal(4, diagnostics.Count(diagnostic => diagnostic.Id == "SYSACC001"));
    }

    [Fact]
    public async Task Computed_access_is_skipped_when_its_permissions_cannot_be_proven()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class ComputedSystem : ISystemicSystem
            {
                public string Id => "computed";
                public IReadOnlyList<SystemAccess> Access => ResolveAccess();
                private static IReadOnlyList<SystemAccess> ResolveAccess() => [];
                public void Step(SystemicSystemContext context) => _ = context.GetWritableWorld();
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Mutable_private_static_access_arrays_are_not_treated_as_proven()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class ReadonlyArraySystem : ISystemicSystem
            {
                private static readonly SystemAccess[] s_access = [SystemAccess.ReadWorld];
                public string Id => "readonly-array";
                public IReadOnlyList<SystemAccess> Access => s_access;
                public void Step(SystemicSystemContext context) => _ = context.GetWritableWorld();
            }
            internal sealed class MutableArraySystem : ISystemicSystem
            {
                private static SystemAccess[] s_access = [SystemAccess.ReadWorld];
                public string Id => "mutable-array";
                public IReadOnlyList<SystemAccess> Access => s_access;
                public void Step(SystemicSystemContext context) => _ = context.GetWritableWorld();
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Write_resource_matches_world_only_for_the_constant_world_sentinel()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class WorldResourceSystem : ISystemicSystem
            {
                public string Id => "world-resource";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteResource("$world")];
                public void Step(SystemicSystemContext context) => _ = context.GetWritableWorld();
            }
            internal sealed class ResourceWriterSystem : ISystemicSystem
            {
                public string Id => "resource-writer";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteResource("custom")];
                public void Step(SystemicSystemContext context) => _ = context.GetWritableWorld();
            }
            internal sealed class DynamicResourceSystem : ISystemicSystem
            {
                public string Id => "dynamic-resource";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteResource(ResolveResource())];
                private static string ResolveResource() => "$world";
                public void Step(SystemicSystemContext context) => _ = context.GetWritableWorld();
            }
            """;

        var diagnostics = await Analyze(source);
        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "SYSACC001"));
        Assert.Contains("ResourceWriterSystem", source.Substring(0, diagnostic.Location.SourceSpan.Start));
    }

    [Fact]
    public async Task Immutable_array_access_fields_prove_world_permissions()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class WorldWriterSystem : ISystemicSystem
            {
                private static readonly ImmutableArray<SystemAccess> s_access = [SystemAccess.WriteWorld];
                public string Id => "writer";
                public IReadOnlyList<SystemAccess> Access => s_access;
                public void Step(SystemicSystemContext context) => _ = context.GetWritableWorld();
            }
            internal sealed class ReadOnlySystem : ISystemicSystem
            {
                private static readonly ImmutableArray<SystemAccess> s_access = [SystemAccess.ReadWorld];
                public string Id => "reader";
                public IReadOnlyList<SystemAccess> Access => s_access;
                public void Step(SystemicSystemContext context) => _ = context.GetWritableWorld();
            }
            """;

        Assert.Single((await Analyze(source)).Where(diagnostic => diagnostic.Id == "SYSACC001"));
    }

    [Fact]
    public async Task Simple_private_static_readonly_generated_column_access_is_analyzed()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Collections.Immutable;
            using Systemic.Host;
            using Systemic.Kernel;

            namespace StaticColumnAccessProof;

            [PopulationSchema("static-columns.items", 1)]
            public partial struct Item
            {
                [PopulationColumn("value")] public int Value;
                [PopulationColumn("phase", DoubleBuffered = true)] public int Phase;
            }

            internal sealed class AllowedSystem : ISystemicSystem
            {
                private static readonly ImmutableArray<SystemAccess> s_access =
                [
                    ItemPopulation.ReadValue,
                    ItemPopulation.WritePhase
                ];
                private readonly Population<Item> _population = null!;
                public string Id => "allowed";
                public IReadOnlyList<SystemAccess> Access => s_access;
                public void Step(SystemicSystemContext context)
                {
                    var columns = ItemPopulation.Bind(_population);
                    _ = columns.Value.ReadOnlySpan[0];
                    columns.Phase.EditNext(0, 1)[0] = 1;
                }
            }

            internal sealed class MissingColumnSystem : ISystemicSystem
            {
                private static readonly ImmutableArray<SystemAccess> s_access = [ItemPopulation.ReadValue];
                private readonly Population<Item> _population = null!;
                public string Id => "missing";
                public IReadOnlyList<SystemAccess> Access => s_access;
                public void Step(SystemicSystemContext context)
                {
                    var columns = ItemPopulation.Bind(_population);
                    _ = columns.Phase.Current[0];
                }
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);
        Assert.Single(diagnostics.Where(diagnostic => diagnostic.Id == "SYSACC004"));
    }

    [Fact]
    public async Task Deferred_population_commands_accept_deferred_or_exclusive_world_access()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal readonly struct SampleKind : IPopulationKind, IPopulationSchema<SampleKind>
            {
                public static string Id => "sample";
                public static int Version => 1;
                public static void InitializeRange(Population<SampleKind> population, PopulationRange range, ReadOnlySpan<SampleKind> values) { }
            }
            internal sealed class DefaultSystem : ISystemicSystem
            {
                public string Id => "default";
                private readonly Population<SampleKind> _population = null!;
                public void Step(SystemicSystemContext context) => _ = context.DeferPopulationCommands(_population, 1);
            }
            internal sealed class DeferredSystem : ISystemicSystem
            {
                public string Id => "deferred";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];
                private readonly Population<SampleKind> _population = null!;
                public void Step(SystemicSystemContext context) => _ = context.DeferPopulationCommands(_population, 1);
            }
            internal sealed class ExclusiveSystem : ISystemicSystem
            {
                public string Id => "exclusive";
                public IReadOnlyList<SystemAccess> Access => new SystemAccess[] { SystemAccess.WriteWorld };
                private readonly Population<SampleKind> _population = null!;
                public void Step(SystemicSystemContext context) => _ = context.DeferPopulationCommands(_population, 1);
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Deferred_population_commands_require_a_world_write_access_mode()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal readonly struct SampleKind : IPopulationKind, IPopulationSchema<SampleKind>
            {
                public static string Id => "sample";
                public static int Version => 1;
                public static void InitializeRange(Population<SampleKind> population, PopulationRange range, ReadOnlySpan<SampleKind> values) { }
            }
            internal sealed class RestrictedSystem : ISystemicSystem
            {
                public string Id => "restricted";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.ReadWorld];
                private readonly Population<SampleKind> _population = null!;
                public void Step(SystemicSystemContext context) => _ = context.DeferPopulationCommands(_population, 1);
            }
            internal sealed class ComputedSystem : ISystemicSystem
            {
                public string Id => "computed";
                public IReadOnlyList<SystemAccess> Access => ResolveAccess();
                private static IReadOnlyList<SystemAccess> ResolveAccess() => [];
                private readonly Population<SampleKind> _population = null!;
                public void Step(SystemicSystemContext context) => _ = context.DeferPopulationCommands(_population, 1);
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Equal(1, diagnostics.Count(diagnostic => diagnostic.Id == "SYSACC002"));
    }

    [Fact]
    public async Task Direct_population_edits_in_range_callbacks_are_flagged_but_generated_writers_are_allowed()
    {
        const string source = """
            using Systemic.Host;
            using Systemic.Kernel;

            namespace AccessProof;

            [PopulationSchema("access.items", 1)]
            public partial struct Item
            {
                [PopulationColumn("value")]
                public int Value;
                [PopulationColumn("phase", DoubleBuffered = true)]
                public int Phase;
            }

            internal sealed class RangeSystem : ISystemicSystem
            {
                public string Id => "range";
                public void Step(SystemicSystemContext context)
                {
                    var population = ItemPopulation.Define(context.GetWritableWorld());
                    var columns = ItemPopulation.Bind(population);
                    context.ForEachRange(columns, 1, static (state, _, range) =>
                        state.Value.EditRange(range.Start, range.Length).Fill(1));
                    context.ForEachRange(columns, 1, static (state, _, range) =>
                        state.Phase.EditNext(range.Start, range.Length).Fill(1));
                    context.ForEachRange(columns, 1, static (state, _, range) =>
                        state.WriteRange(range).Write(0, new ItemPopulation.Row { Value = 2 }));
                    columns.Value.EditRange(0, 1).Fill(3);
                    columns.Phase.EditNext(0, 1).Fill(3);
                }
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);
        var rangeDiagnostics = diagnostics.Where(item => item.Id == "SYSACC003").ToArray();
        Assert.Equal(2, rangeDiagnostics.Length);
        Assert.Contains(rangeDiagnostics, diagnostic => At(diagnostic, source).Contains("EditRange"));
        Assert.Contains(rangeDiagnostics, diagnostic => At(diagnostic, source).Contains("EditNext"));
        Assert.All(rangeDiagnostics, diagnostic => Assert.Contains("WriteRange(range).Write", diagnostic.GetMessage()));
    }

    [Fact]
    public async Task Direct_memory_two_buffer_and_swap_mutators_are_flagged_only_inside_range_callbacks()
    {
        const string source = """
            using Systemic.Host;
            using Systemic.Kernel;

            namespace RangeMutatorProof;

            [PopulationSchema("range.items", 1)]
            public partial struct Item
            {
                [PopulationColumn("value")] public int Value;
                [PopulationColumn("phase", DoubleBuffered = true)] public int Phase;
            }

            internal sealed class RangeSystem : ISystemicSystem
            {
                public string Id => "range";
                public void Step(SystemicSystemContext context)
                {
                    var population = ItemPopulation.Define(context.GetWritableWorld());
                    var columns = ItemPopulation.Bind(population);
                    context.ForEachRange(columns, 1, static (state, _, range) =>
                        state.Value.EditMemory(range.Start, range.Length).Span.Fill(1));
                    context.ForEachRange(columns, 1, static (state, _, range) =>
                        state.Phase.EditNextMemory(range.Start, range.Length).Span.Fill(2));
                    context.ForEachRange(columns, 1, static (state, _, range) =>
                        state.Phase.EditBothBuffers(range.Start, range.Length).Next.Fill(3));
                    context.ForEachRange(columns, 1, static (state, _, _) => state.Phase.Swap());

                    columns.Value.EditMemory(0, 1).Span.Fill(4);
                    columns.Phase.EditNextMemory(0, 1).Span.Fill(5);
                    columns.Phase.EditBothBuffers(0, 1).Next.Fill(6);
                    columns.Phase.Swap();
                }
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);
        var rangeDiagnostics = diagnostics.Where(item => item.Id == "SYSACC003").ToArray();
        Assert.Equal(4, rangeDiagnostics.Length);
        foreach (var methodName in new[] { "EditMemory", "EditNextMemory", "EditBothBuffers", "Swap" })
            Assert.Contains(rangeDiagnostics, diagnostic => At(diagnostic, source).Contains(methodName));
        Assert.Contains("Initialize both buffers outside", rangeDiagnostics.Single(diagnostic => At(diagnostic, source).Contains("EditBothBuffers")).GetMessage());
        Assert.Contains("Move buffer swaps outside", rangeDiagnostics.Single(diagnostic => At(diagnostic, source).Contains("Swap")).GetMessage());
    }

    [Fact]
    public async Task Similarly_named_unrelated_apis_do_not_report_access_diagnostics()
    {
        const string source = """
            using System;
            using Systemic.Kernel;
            namespace UserTypes
            {
                internal sealed class SystemicSystemContext
                {
                    public void ForEachRange<TState>(TState state, int rowCount, Action<TState, int, PopulationRange> callback) => callback(state, 0, default);
                }
                internal sealed class PopulationColumn<T>
                {
                    public Span<T> EditRange(int start, int length) => throw new NotImplementedException();
                }
                internal sealed class UserSystem
                {
                    public void Run(SystemicSystemContext context, PopulationColumn<int> column) =>
                        context.ForEachRange(column, 1, static (state, _, range) => state.EditRange(range.Start, range.Length));
                }
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Direct_generated_column_reads_and_writes_must_match_literal_access_entries()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;

            namespace ColumnAccessProof;

            [PopulationSchema("columns.items", 1)]
            public partial struct Item
            {
                [PopulationColumn("value")] public int Value;
                [PopulationColumn("phase", DoubleBuffered = true)] public int Phase;
            }

            internal sealed class ReadAllowedSystem : ISystemicSystem
            {
                private readonly Population<Item> _population = null!;
                public string Id => "read-allowed";
                public IReadOnlyList<SystemAccess> Access => [ItemPopulation.ReadValue];
                public void Step(SystemicSystemContext context)
                {
                    var columns = ItemPopulation.Bind(_population);
                    _ = columns.Value.ReadOnlySpan[0];
                }
            }

            internal sealed class MissingAccessSystem : ISystemicSystem
            {
                private readonly Population<Item> _population = null!;
                public string Id => "missing";
                public IReadOnlyList<SystemAccess> Access => [ItemPopulation.ReadValue];
                public void Step(SystemicSystemContext context)
                {
                    var columns = ItemPopulation.Bind(_population);
                    _ = columns.Phase.Current[0];
                    columns.Value.EditRange(0, 1)[0] = 1;
                }
            }

            internal sealed class WriteAccessSystem : ISystemicSystem
            {
                private readonly Population<Item> _population = null!;
                public string Id => "write";
                public IReadOnlyList<SystemAccess> Access => [ItemPopulation.WritePhase];
                public void Step(SystemicSystemContext context)
                {
                    var columns = ItemPopulation.Bind(_population);
                    _ = columns.Phase.Current[0];
                    columns.Phase.EditNext(0, 1)[0] = 2;
                }
            }

            internal sealed class WorldWildcardSystem : ISystemicSystem
            {
                private readonly Population<Item> _population = null!;
                public string Id => "wildcard";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];
                public void Step(SystemicSystemContext context)
                {
                    var columns = ItemPopulation.Bind(_population);
                    columns.Value.EditRange(0, 1)[0] = 3;
                }
            }

            internal sealed class ComputedAccessSystem : ISystemicSystem
            {
                private readonly Population<Item> _population = null!;
                public string Id => "computed";
                public IReadOnlyList<SystemAccess> Access => ResolveAccess();
                private static IReadOnlyList<SystemAccess> ResolveAccess() => [];
                public void Step(SystemicSystemContext context)
                {
                    var columns = ItemPopulation.Bind(_population);
                    _ = columns.Value.ReadOnlySpan[0];
                    columns.Value.EditRange(0, 1)[0] = 4;
                }
            }

            internal sealed class UserColumnsSystem : ISystemicSystem
            {
                private readonly UserColumns _columns = new();
                public string Id => "user-columns";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.ReadWorld];
                public void Step(SystemicSystemContext context) => _ = _columns.Value.ReadOnlySpan[0];
            }

            internal sealed class UserColumns
            {
                public PopulationColumn<int> Value { get; } = null!;
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);
        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Id == "SYSACC004"));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SYSACC004" && diagnostic.GetMessage().Contains("read"));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SYSACC004" && diagnostic.GetMessage().Contains("write"));
    }

    [Fact]
    public async Task Resource_and_unrelated_generic_column_access_do_not_disable_column_checks()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;

            namespace ColumnAccessWithResourceProof;

            [PopulationSchema("resource-proof.items", 1)]
            public partial struct Item
            {
                [PopulationColumn("value")] public int Value;
                [PopulationColumn("phase", DoubleBuffered = true)] public int Phase;
            }

            internal sealed class MissingColumnAccessSystem : ISystemicSystem
            {
                private readonly Population<Item> _population = null!;
                public string Id => "missing-column-access";
                public IReadOnlyList<SystemAccess> Access =>
                    [SystemAccess.ReadResource("clock"), SystemAccess.ReadColumn<Item, int>(ItemPopulation.ValueKey)];
                public void Step(SystemicSystemContext context)
                {
                    var columns = ItemPopulation.Bind(_population);
                    _ = columns.Value.ReadOnlySpan[0];
                    _ = columns.Phase.Current[0];
                }
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "SYSACC004"));
        Assert.Contains("Phase", diagnostic.GetMessage(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Immediate_world_operations_and_shared_commands_are_forbidden_in_range_callbacks()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class UnsafeSystem : ISystemicSystem
            {
                public string Id => "unsafe";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];
                public void Step(SystemicSystemContext context)
                {
                    context.Commands.AddTag(1, "outside");
                    context.ForEachRange(context, 1, static (state, _, _) =>
                    {
                        _ = state.GetWritableWorld();
                        state.SetProperty(null!, "value", 1);
                        state.Publish(default);
                        state.Execute(default);
                        state.Commands.AddTag(1, "inside");
                    });
                }
            }
            internal sealed class AliasSystem : ISystemicSystem
            {
                public string Id => "alias";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];
                public void Step(SystemicSystemContext context)
                {
                    var commands = context.Commands;
                    context.ForEachRange(commands, 1, static (state, _, _) => state.AddTag(1, "alias"));
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Equal(5, diagnostics.Count(diagnostic => diagnostic.Id == "SYSACC005"));
    }

    private static string At(Diagnostic diagnostic, string source) => source.Substring(
        diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, bool generatePopulationSchema = false)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Systemic.Host.SystemicSystemContext).Assembly.Location));
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var tree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "access-proof.cs");
        var compilation = CSharpCompilation.Create("SystemAccessProof", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        if (generatePopulationSchema)
        {
            GeneratorDriver driver = CSharpGeneratorDriver.Create(
                [new PopulationSchemaGenerator().AsSourceGenerator()], parseOptions: parseOptions);
            driver.RunGeneratorsAndUpdateCompilation(compilation, out var generatedCompilation, out var generatorDiagnostics);
            compilation = (CSharpCompilation)generatedCompilation;
            Assert.Empty(generatorDiagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }

        Assert.Empty(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        var analyzerType = typeof(StrictDeterminismAnalyzer).Assembly
            .GetType("Systemic.Kernel.Generator.SystemAccessAnalyzer");
        if (analyzerType is null)
            return ImmutableArray<Diagnostic>.Empty;

        var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType)!;
        return await compilation.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync();
    }
}
