using System.Globalization;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace Systemic.Kernel.Generator.Tests;

public sealed class SystemAccessAnalyzerTests
{
    [Fact]
    public void DiagnosticsSitInTheSystemAccessCategoryAndLinkToTheGuide()
    {
        var analyzerType = typeof(StrictDeterminismAnalyzer).Assembly
            .GetType("Systemic.Kernel.Generator.SystemAccessAnalyzer");
        Assert.NotNull(analyzerType);
        var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType)!;
        var diagnostics = analyzer.SupportedDiagnostics;

        Assert.Equal(8, diagnostics.Length);
        Assert.Equal(7, diagnostics.Count(descriptor => descriptor.DefaultSeverity == DiagnosticSeverity.Error));
        var exclusive = Assert.Single(diagnostics.Where(descriptor => descriptor.DefaultSeverity == DiagnosticSeverity.Info));
        Assert.Equal("SYSACC007", exclusive.Id);
        foreach (var descriptor in diagnostics)
        {
            Assert.Equal("SystemAccess", descriptor.Category);
            Assert.True(descriptor.IsEnabledByDefault);
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Description.ToString(CultureInfo.InvariantCulture)));
            Assert.Equal(
                $"https://github.com/cakegod/systemic-framework/blob/main/docs/api/analyzers.md#{descriptor.Id.ToLowerInvariant()}",
                descriptor.HelpLinkUri);
        }
    }

    [Fact]
    public async Task DefaultAccessAllowsImmediateWorldOperations()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            internal sealed class DefaultSystem : ISystemicSystem
            {
                public string Id => "default";
                public void Step(SystemicSystemContext context)
                {
                    _ = context.GetWritableWorld();
                    context.SetProperty(null!, "value", 1);
                    context.Publish(default);
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal("SYSACC007", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task SystemsDeclaringAccessDoNotReportDefaultExclusivity()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class DeclaredSystem : ISystemicSystem
            {
                public string Id => "declared";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld];
                public void Step(SystemicSystemContext context) { }
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task DirectWriteWorldAccessAllowsImmediateWorldOperations()
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
    public async Task MissingWriteWorldAccessReportsEachImmediateWorldOperation()
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
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Equal(3, diagnostics.Count(diagnostic => diagnostic.Id == "SYSACC001"));
    }

    [Fact]
    public async Task ComputedAccessIsSkippedWhenItsPermissionsCannotBeProven()
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
    public async Task MutablePrivateStaticAccessArraysAreNotTreatedAsProven()
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
    public async Task WriteResourceMatchesWorldOnlyForTheConstantWorldSentinel()
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
        Assert.Contains("ResourceWriterSystem", source[..diagnostic.Location.SourceSpan.Start]);
    }

    [Fact]
    public async Task ImmutableArrayAccessFieldsProveWorldPermissions()
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
    public async Task SimplePrivateStaticReadonlyGeneratedColumnAccessIsAnalyzed()
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
                    var lease = context.BorrowPopulation(_population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> access) =>
                        {
                            _ = access.ReadAll(state.Value)[0];
                            access.EditNext(state.Phase)[0] = 1;
                        });
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
                    var lease = context.BorrowPopulation(_population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> access) =>
                            _ = access.ReadCurrent(state.Phase)[0]);
                }
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);
        Assert.Single(diagnostics.Where(diagnostic => diagnostic.Id == "SYSACC004"));
    }

    [Fact]
    public async Task DeferredPopulationCommandsAcceptDeferredOrExclusiveWorldAccess()
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

        var diagnostics = await Analyze(source);
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        Assert.Equal("SYSACC007", Assert.Single(diagnostics).Id);
    }

    [Fact]
    public async Task DeferredFactsRequireAWorldWriteAccessMode()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class RestrictedSystem : ISystemicSystem
            {
                public string Id => "restricted";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.ReadWorld];
                public void Step(SystemicSystemContext context) => context.DeferFact(default);
            }
            internal sealed class DeferredSystem : ISystemicSystem
            {
                public string Id => "deferred";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.DeferWorldWrites];
                public void Step(SystemicSystemContext context) => context.DeferFact(default);
            }
            internal sealed class ExclusiveSystem : ISystemicSystem
            {
                public string Id => "exclusive";
                public IReadOnlyList<SystemAccess> Access => new SystemAccess[] { SystemAccess.WriteWorld };
                public void Step(SystemicSystemContext context) => context.DeferFact(default);
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Single(diagnostics.Where(diagnostic => diagnostic.Id == "SYSACC008"));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "SYSACC007");
    }

    [Fact]
    public async Task DeferredPopulationCommandsRequireAWorldWriteAccessMode()
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
    public async Task ScopedWritesAreAllowedButBufferSwapsAreFlaggedInsideRangeCallbacks()
    {
        const string source = """
            using System.Collections.Generic;
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
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld, ItemPopulation.WriteValue, ItemPopulation.WritePhase];
                public void Step(SystemicSystemContext context)
                {
                    var population = ItemPopulation.Define(context.GetWritableWorld());
                    var columns = ItemPopulation.Bind(population);
                    var lease = context.BorrowPopulation(population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> scope) =>
                        {
                            scope.EditRange(state.Value).Fill(1);
                            scope.EditNext(state.Phase).Fill(1);
                            state.WriteRange(ref scope).Write(0, new ItemPopulation.Row { Value = 2 });
                            state.Phase.Swap();
                        });
                }
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);
        var rangeDiagnostics = diagnostics.Where(item => item.Id == "SYSACC003").ToArray();
        Assert.Single(rangeDiagnostics);
        Assert.Contains(rangeDiagnostics, diagnostic => At(diagnostic, source).Contains("Swap"));
        Assert.Contains("WriteRange(ref scope)", rangeDiagnostics[0].GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task FullSingleBufferedReadIsRejectedWhenItsParallelRangeCallbackWritesTheColumn()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;

            namespace ParallelReadProof;

            [PopulationSchema("parallel.items", 1)]
            public partial struct Item
            {
                [PopulationColumn("value")] public int Value;
            }

            internal sealed class RangeSystem : ISystemicSystem
            {
                public string Id => "parallel-read";
                public IReadOnlyList<SystemAccess> Access =>
                    [ItemPopulation.ReadValue, ItemPopulation.WriteValue];
                public void Step(SystemicSystemContext context)
                {
                    var population = ItemPopulation.Define(context.GetWritableWorld());
                    var columns = ItemPopulation.Bind(population);
                    var lease = context.BorrowPopulation(population);
                    var alias = context.BorrowPopulation(population);
                    context.ForEachPopulationRange(lease, (columns, alias),
                        static ((ItemPopulation.Columns Columns, PopulationLease<Item> Lease) state,
                            int partition, scoped ref PopulationAccessScope<Item> scope) =>
                        {
                            scope.ReadAll(state.Lease, state.Columns.Value);
                            scope.EditRange(state.Columns.Value).Fill(1);
                        });
                }
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);
        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "SYSACC006"));
        Assert.Contains("ReadRange", diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.Contains("ReadAll", At(diagnostic, source));
    }

    [Fact]
    public async Task BothBufferEditsAndSwapsAreFlaggedOnlyInsidePopulationRangeCallbacks()
    {
        const string source = """
            using System.Collections.Generic;
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
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.WriteWorld, ItemPopulation.WritePhase];
                public void Step(SystemicSystemContext context)
                {
                    var population = ItemPopulation.Define(context.GetWritableWorld());
                    var columns = ItemPopulation.Bind(population);
                    var lease = context.BorrowPopulation(population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> access) =>
                        {
                            access.EditBoth(state.Phase).Next.Fill(3);
                            state.Phase.Swap();
                        });
                    columns.Phase.Swap();
                }
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);
        var rangeDiagnostics = diagnostics.Where(item => item.Id == "SYSACC003").ToArray();
        Assert.Equal(2, rangeDiagnostics.Length);
        foreach (var methodName in new[] { "EditBoth", "Swap" })
        {
            Assert.Contains(rangeDiagnostics, diagnostic => At(diagnostic, source).Contains(methodName));
        }

        Assert.Contains("Initialize both buffers outside", rangeDiagnostics.Single(diagnostic => At(diagnostic, source).Contains("EditBoth")).GetMessage(CultureInfo.InvariantCulture));
        Assert.Contains("Move buffer swaps outside", rangeDiagnostics.Single(diagnostic => At(diagnostic, source).Contains("Swap")).GetMessage(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SimilarlyNamedUnrelatedApisDoNotReportAccessDiagnostics()
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
    public async Task DirectGeneratedColumnReadsAndWritesMustMatchLiteralAccessEntries()
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
                    var lease = context.BorrowPopulation(_population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> access) =>
                            _ = access.ReadAll(state.Value)[0]);
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
                    var lease = context.BorrowPopulation(_population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> access) =>
                        {
                            _ = access.ReadCurrent(state.Phase)[0];
                            access.EditRange(state.Value)[0] = 1;
                        });
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
                    var lease = context.BorrowPopulation(_population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> access) =>
                        {
                            _ = access.ReadCurrent(state.Phase)[0];
                            access.EditNext(state.Phase)[0] = 2;
                        });
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
                    var lease = context.BorrowPopulation(_population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> access) =>
                            access.EditRange(state.Value)[0] = 3);
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
                    var lease = context.BorrowPopulation(_population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> access) =>
                        {
                            _ = access.ReadAll(state.Value)[0];
                            access.EditRange(state.Value)[0] = 4;
                        });
                }
            }

            internal sealed class UserColumnsSystem : ISystemicSystem
            {
                private readonly UserColumns _columns = new();
                public string Id => "user-columns";
                public IReadOnlyList<SystemAccess> Access => [SystemAccess.ReadWorld];
                public void Step(SystemicSystemContext context) => _ = _columns.Value;
            }

            internal sealed class UserColumns
            {
                public int Value { get; } = 0;
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);
        Assert.Equal(3, diagnostics.Count(diagnostic => diagnostic.Id == "SYSACC004"));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SYSACC004" && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("read"));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "SYSACC004" && diagnostic.GetMessage(CultureInfo.InvariantCulture).Contains("write"));
    }

    [Fact]
    public async Task ResourceAndUnrelatedGenericColumnAccessDoNotDisableColumnChecks()
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
                    var lease = context.BorrowPopulation(_population);
                    context.ForEachPopulationRange(lease, columns,
                        static (ItemPopulation.Columns state, int _, scoped ref PopulationAccessScope<Item> access) =>
                        {
                            _ = access.ReadAll(state.Value)[0];
                            _ = access.ReadCurrent(state.Phase)[0];
                        });
                }
            }
            """;

        var diagnostics = await Analyze(source, generatePopulationSchema: true);

        var diagnostic = Assert.Single(diagnostics.Where(item => item.Id == "SYSACC004"));
        Assert.Contains("Phase", diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImmediateWorldOperationsAndSharedCommandsAreForbiddenInRangeCallbacks()
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
        Assert.Equal(4, diagnostics.Count(diagnostic => diagnostic.Id == "SYSACC005"));
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
        {
            return [];
        }

        var analyzer = (DiagnosticAnalyzer)Activator.CreateInstance(analyzerType)!;
        return await compilation.WithAnalyzers([analyzer]).GetAnalyzerDiagnosticsAsync();
    }
}
