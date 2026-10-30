using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Systemic.Kernel.Generator.Tests;

public sealed class PopulationSchemaGeneratorTests
{
    [Fact]
    public void GeneratesATypedPopulationFacadeForAnAnnotatedPartialStruct()
    {
        const string source = """
            using System.Numerics;
            using Systemic.Kernel;

            namespace Proof
            {
                [PopulationSchema("proof.particles", 3, EnableHandles = true)]
                public partial struct Particle
                {
                    [PopulationColumn("position", DoubleBuffered = true)]
                    public Vector2 Position;

                    [PopulationColumn("energy")]
                    public float Energy;
                }

                public static class Consumer
                {
                    public static int Bind(World world)
                    {
                        var population = ParticlePopulation.Define(world);
                        var columns = ParticlePopulation.Bind(population);
                        var rows = population.AddRange(2);
                        using (var lease = population.Borrow())
                        {
                            lease.WithAccess(rows, columns, static (ParticlePopulation.Columns state, scoped ref PopulationAccessScope<Particle> access) =>
                            {
                                access.EditNext(state.Position).Clear();
                                access.EditRange(state.Energy).Fill(1.0f);
                            });
                        }
                        var commands = new PopulationCommandBuffer<Particle>(population);
                        commands.Reserve(spawnCapacity: 1, removalCapacity: 1);
                        commands.Spawn([new Particle { Position = new Vector2(13, 17), Energy = 0.5f }]);
                        commands.Flush();
                        var handle = population.GetHandle(2);
                        return population.TryResolve(handle, out var row) ? population.Count + row : -1;
                    }

                    public static string AccessId() => ParticlePopulation.WritePosition.Resource;
                }
            }
            """;

        var (driver, output) = Generate(source);

        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        using var assembly = new MemoryStream();
        var emit = output.Emit(assembly);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var generatedAssembly = System.Reflection.Assembly.Load(assembly.ToArray());
        var bind = generatedAssembly.GetType("Proof.Consumer")!.GetMethod("Bind")!;
        Assert.Equal(5, bind.Invoke(null, [new World(seed: 1, valueScale: 1)]));
        var accessId = generatedAssembly.GetType("Proof.Consumer")!.GetMethod("AccessId")!;
        Assert.Equal("population:proof.particles/column:position", accessId.Invoke(null, null));
    }

    [Fact]
    public void SnapshotCopiesARangeIntoACallerBuffer()
    {
        const string source = """
            using Systemic.Kernel;

            namespace SnapshotProof;

            [PopulationSchema("proof.snapshot", 1)]
            public partial struct Particle
            {
                [PopulationColumn("x", DoubleBuffered = true)] public int X;
                [PopulationColumn("health")] public int Health;
            }

            public static class Consumer
            {
                public static int Run(World world)
                {
                    var population = ParticlePopulation.Define(world);
                    var range = population.AddRange(3);
                    Particle.InitializeRange(population, range, new Particle[]
                    {
                        new() { X = 10, Health = 1 },
                        new() { X = 20, Health = 2 },
                        new() { X = 30, Health = 3 }
                    });
                    var snapshot = new Particle[2];
                    ParticlePopulation.Snapshot(population, new PopulationRange(1, 2), snapshot);
                    return (snapshot[0].X * 100) + snapshot[1].Health;
                }
            }
            """;

        var (driver, output) = Generate(source);

        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        using var assembly = new MemoryStream();
        var emit = output.Emit(assembly);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var consumer = System.Reflection.Assembly.Load(assembly.ToArray()).GetType("SnapshotProof.Consumer")!;
        Assert.Equal(2003, consumer.GetMethod("Run")!.Invoke(null, [new World(seed: 1, valueScale: 1)]));
    }

    [Fact]
    public void GeneratedRowAndRangeWritesUseCheckedColumnsAndRestoreTransactionState()
    {
        const string source = """
            using Systemic.Kernel;

            namespace ProjectionProof;

            [PopulationSchema("proof.projections", 1)]
            public partial struct Particle
            {
                [PopulationColumn("x", DoubleBuffered = true)] public int X;
                [PopulationColumn("health")] public int Health;
            }

            public static class Consumer
            {
                public static bool Run(World world)
                {
                    var population = ParticlePopulation.Define(world);
                    var range = population.AddRange(3);
                    Particle.InitializeRange(population, range, new Particle[]
                    {
                        new() { X = 10, Health = 1 },
                        new() { X = 20, Health = 2 },
                        new() { X = 30, Health = 3 }
                    });
                    var columns = ParticlePopulation.Bind(population);
                    var result = new[] { true };
                    using (var lease = population.Borrow())
                    {
                        lease.WithAccess(new PopulationRange(0, 3), (columns, result),
                            static ((ParticlePopulation.Columns Columns, bool[] Result) state, scoped ref PopulationAccessScope<Particle> access) =>
                            {
                                var snapshot = state.Columns.ReadRow(ref access, 1);
                                var changed = snapshot;
                                changed.X = 90;
                                changed.Health = 42;
                                state.Columns.WriteRow(ref access, 1, changed);
                                state.Result[0] = snapshot.X == 20 && snapshot.Health == 2
                                    && state.Columns.ReadRow(ref access, 1).X == 20
                                    && state.Columns.ReadRow(ref access, 1).Health == 42;
                            });
                    }
                    if (!result[0]) return false;
                    columns.X.Swap();
                    using (var lease = population.Borrow())
                    {
                        lease.WithAccess(new PopulationRange(0, 3), (columns, result),
                            static ((ParticlePopulation.Columns Columns, bool[] Result) state, scoped ref PopulationAccessScope<Particle> access) =>
                                state.Result[0] = state.Columns.ReadRow(ref access, 1).X == 90);
                    }
                    if (!result[0]) return false;

                    using (var transaction = world.BeginTransaction())
                    {
                        using (var lease = population.Borrow())
                        {
                            lease.WithAccess(new PopulationRange(0, 2), (columns, result),
                                static ((ParticlePopulation.Columns Columns, bool[] Result) state, scoped ref PopulationAccessScope<Particle> access) =>
                                {
                                    var writer = state.Columns.WriteRange(ref access);
                                    if (writer.Length != 2) { state.Result[0] = false; return; }
                                    writer.Write(0, new ParticlePopulation.Row { X = 101, Health = 201 });
                                    writer.Write(1, new ParticlePopulation.Row { X = 102, Health = 202 });
                                    state.Result[0] = state.Columns.ReadRow(ref access, 0).X == 10
                                        && state.Columns.ReadRow(ref access, 0).Health == 201;
                                });
                        }
                        if (!result[0]) return false;
                        columns.X.Swap();
                        using (var lease = population.Borrow())
                        {
                            lease.WithAccess(new PopulationRange(0, 2), (columns, result),
                                static ((ParticlePopulation.Columns Columns, bool[] Result) state, scoped ref PopulationAccessScope<Particle> access) =>
                                    state.Result[0] = state.Columns.ReadRow(ref access, 0).X == 101
                                        && state.Columns.ReadRow(ref access, 1).X == 102);
                        }
                        if (!result[0]) return false;
                        transaction.Rollback();
                    }
                    using (var lease = population.Borrow())
                    {
                        lease.WithAccess(new PopulationRange(0, 3), (columns, result),
                            static ((ParticlePopulation.Columns Columns, bool[] Result) state, scoped ref PopulationAccessScope<Particle> access) =>
                                state.Result[0] = state.Columns.ReadRow(ref access, 0).X == 10
                                    && state.Columns.ReadRow(ref access, 0).Health == 1
                                    && state.Columns.ReadRow(ref access, 1).X == 90
                                    && state.Columns.ReadRow(ref access, 1).Health == 42
                                    && state.Columns.ReadRow(ref access, 2).X == 30
                                    && state.Columns.ReadRow(ref access, 2).Health == 3);
                    }
                    return result[0];
                }
            }
            """;

        var (driver, output) = Generate(source);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        using var assembly = new MemoryStream();
        var emit = output.Emit(assembly);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var method = System.Reflection.Assembly.Load(assembly.ToArray())
            .GetType("ProjectionProof.Consumer")!.GetMethod("Run")!;
        Assert.Equal(true, method.Invoke(null, [new World(seed: 1, valueScale: 1)]));
    }

    [Fact]
    public void GeneratedInitializerDoesNotAllocateATemporaryInputCopy()
    {
        const string source = """
            using Systemic.Kernel;

            namespace AllocationProof;
            [PopulationSchema("proof.allocations", 1)]
            public partial struct Particle
            {
                [PopulationColumn("position", DoubleBuffered = true)] public int Position;
                [PopulationColumn("health")] public int Health;
            }

            public static class Consumer
            {
                public static long Measure()
                {
                    var world = new World(seed: 44, valueScale: 1);
                    var population = ParticlePopulation.Define(world);
                    var range = population.AddRange(2);
                    Particle[] values = [new() { Position = 10, Health = 1 }, new() { Position = 20, Health = 2 }];
                    Particle.InitializeRange(population, range, values);
                    var before = global::System.GC.GetAllocatedBytesForCurrentThread();
                    Particle.InitializeRange(population, range, values);
                    return global::System.GC.GetAllocatedBytesForCurrentThread() - before;
                }
            }
            """;

        var (driver, output) = Generate(source);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        using var assembly = new MemoryStream();
        var emit = output.Emit(assembly);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var method = System.Reflection.Assembly.Load(assembly.ToArray())
            .GetType("AllocationProof.Consumer")!.GetMethod("Measure")!;

        Assert.Equal(0L, method.Invoke(null, null));
    }

    [Fact]
    public void PopulationScopeAndSpansCannotEscapeTheCallback()
    {
        const string source = """
            using System;
            using Systemic.Kernel;

            internal readonly struct Kind : IPopulationKind
            {
                public static string Id => "proof.escape";
                public static int Version => 1;
            }

            internal static class Consumer
            {
                public static void CaptureScope(Population<Kind> population)
                {
                    var escaped = default(PopulationAccessScope<Kind>);
                    population.WithAccess(new PopulationRange(0, population.Count), 0,
                        (int state, scoped ref PopulationAccessScope<Kind> scope) => escaped = scope);
                }

                public static void CaptureSpan(Population<Kind> population, PopulationColumn<int> column)
                {
                    ReadOnlySpan<int> escaped = default;
                    population.WithAccess(new PopulationRange(0, population.Count), column,
                        (PopulationColumn<int> state, scoped ref PopulationAccessScope<Kind> scope) => escaped = scope.ReadAll(state));
                }
            }
            """;

        var (_, output) = Generate(source);
        Assert.Contains(output.GetDiagnostics(), diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Id == "CS8175");
    }

    [Fact]
    public void GeneratedProjectionsRejectInvalidRowsAndRangesBeforeWriting()
    {
        const string source = """
            using System;
            using Systemic.Kernel;

            namespace BoundsProof;
            [PopulationSchema("proof.bounds", 1)]
            public partial struct Particle
            {
                [PopulationColumn("x", DoubleBuffered = true)] public int X;
                [PopulationColumn("health")] public int Health;
            }
            public static class Consumer
            {
                public static bool Run(World world)
                {
                    var population = ParticlePopulation.Define(world);
                    var range = population.AddRange(2);
                    Particle.InitializeRange(population, range, new Particle[]
                    {
                        new() { X = 10, Health = 1 }, new() { X = 20, Health = 2 }
                    });
                    var columns = ParticlePopulation.Bind(population);
                    var result = new[] { false };
                    using (var lease = population.Borrow())
                    {
                        lease.WithAccess(new PopulationRange(0, 2), (columns, result),
                            static ((ParticlePopulation.Columns Columns, bool[] Result) state, scoped ref PopulationAccessScope<Particle> access) =>
                            {
                                var failures = 0;
                                try { state.Columns.ReadRow(ref access, -1); } catch (ArgumentOutOfRangeException) { failures++; }
                                try { state.Columns.WriteRow(ref access, 2, new ParticlePopulation.Row()); } catch (ArgumentOutOfRangeException) { failures++; }
                                var writer = state.Columns.WriteRange(ref access);
                                try { writer.Write(2, new ParticlePopulation.Row()); } catch (ArgumentOutOfRangeException) { failures++; }
                                state.Result[0] = failures == 3 && writer.Length == 2
                                    && state.Columns.ReadRow(ref access, 0).Health == 1
                                    && state.Columns.ReadRow(ref access, 1).Health == 2;
                            });
                    }
                    return result[0];
                }
            }
            """;

        var (driver, output) = Generate(source);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        using var assembly = new MemoryStream();
        var emit = output.Emit(assembly);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var method = System.Reflection.Assembly.Load(assembly.ToArray())
            .GetType("BoundsProof.Consumer")!.GetMethod("Run")!;
        Assert.Equal(true, method.Invoke(null, [new World(seed: 1, valueScale: 1)]));
    }

    [Fact]
    public void SchemasWithTheSameNameInDifferentNamespacesGenerateIndependently()
    {
        const string source = """
            using Systemic.Kernel;
            namespace First { [PopulationSchema("first", 1)] public partial struct Particle
                { [PopulationColumn("value")] public int Value; } }
            namespace Second { [PopulationSchema("second", 1)] public partial struct Particle
                { [PopulationColumn("value")] public int Value; } }
            """;

        var (driver, output) = Generate(source);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Equal(2, driver.GetRunResult().Results.Single().GeneratedSources.Length);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void SchemaNamespacesThatDifferOnlyByCaseDoNotCollide()
    {
        const string source = """
            using Systemic.Kernel;
            namespace First { [PopulationSchema("first.upper", 1)] public partial struct Particle
                { [PopulationColumn("value")] public int Value; } }
            namespace first { [PopulationSchema("first.lower", 1)] public partial struct Particle
                { [PopulationColumn("value")] public int Value; } }
            """;
        var (driver, output) = Generate(source);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Equal(2, driver.GetRunResult().Results.Single().GeneratedSources.Length);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void GeneratedProjectionsSupportKeywordsAndFieldsNamedLikeLocalParameters()
    {
        const string source = """
            using Systemic.Kernel;
            namespace @class;
            [PopulationSchema("proof.names", 1)] public partial struct Particle
            {
                [PopulationColumn("row")] public int row;
                [PopulationColumn("range")] public int range;
                [PopulationColumn("population")] public int population;
                [PopulationColumn("class")] public int @class;
                [PopulationColumn("accent")] public int á;
                [PopulationColumn("underscore")] public int a_;
            }
            public static class Consumer
            {
                public static int Run(World world)
                {
                    var population = ParticlePopulation.Define(world);
                    var range = population.AddRange(1);
                    Particle.InitializeRange(population, range, new Particle[]
                        { new() { row = 1, range = 2, population = 3, @class = 4, á = 5, a_ = 6 } });
                    var columns = ParticlePopulation.Bind(population);
                    var result = new int[1];
                    using (var lease = population.Borrow())
                    {
                        lease.WithAccess(range, (columns, result),
                            static ((ParticlePopulation.Columns Columns, int[] Result) state, scoped ref PopulationAccessScope<Particle> access) =>
                            {
                                var value = state.Columns.ReadRow(ref access, 0);
                                value.row += 10;
                                state.Columns.WriteRow(ref access, 0, value);
                                value = state.Columns.ReadRow(ref access, 0);
                                state.Result[0] = value.row + value.range + value.population + value.@class + value.á + value.a_;
                            });
                    }
                    return result[0];
                }
            }
            """;
        var (driver, output) = Generate(source);
        Assert.Empty(driver.GetRunResult().Diagnostics);
        Assert.Empty(output.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        using var assembly = new MemoryStream();
        var emit = output.Emit(assembly);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
        var method = System.Reflection.Assembly.Load(assembly.ToArray()).GetType("class.Consumer")!.GetMethod("Run")!;
        Assert.Equal(31, method.Invoke(null, [new World(seed: 1, valueScale: 1)]));
    }

    [Theory]
    [InlineData("public struct Particle { [PopulationColumn(\"energy\")] public float Energy; }", "partial")]
    [InlineData("public partial struct Particle { [PopulationColumn(\"name\")] public string Name; }", "unmanaged")]
    public void ReportsInvalidSchemaInsteadOfEmittingBrokenSource(string declaration, string message)
    {
        var source = $$"""
            using Systemic.Kernel;

            namespace Proof
            {
                [PopulationSchema("proof.invalid", 1)]
                {{declaration}}
            }
            """;

        var (driver, _) = Generate(source);
        var result = Assert.Single(driver.GetRunResult().Results);

        Assert.Null(result.Exception);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("PTBK001", diagnostic.Id);
        Assert.Contains(message, diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.GeneratedSources);
    }

    [Theory]
    [InlineData("[PopulationColumn(\"x\")] public int ReadRow;", "ReadRow")]
    [InlineData("[PopulationColumn(\"x\")] public int Row;", "Row")]
    [InlineData("[PopulationColumn(\"x\")] public int Columns;", "Columns")]
    [InlineData("[PopulationColumn(\"x\")] public int ValidateOffset;", "ValidateOffset")]
    [InlineData("[PopulationColumn(\"x\")] public int FooKey; [PopulationColumn(\"y\")] public int ReadFoo;", "ReadFooKey")]
    public void ReportsGeneratedMemberNameConflicts(string fields, string conflictingName)
    {
        var source = $$"""
            using Systemic.Kernel;
            [PopulationSchema("proof.conflict", 1)] public partial struct Particle { {{fields}} }
            """;
        var (driver, _) = Generate(source);
        var result = Assert.Single(driver.GetRunResult().Results);
        Assert.Null(result.Exception);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("PTBK001", diagnostic.Id);
        Assert.Contains(conflictingName, diagnostic.GetMessage(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.Empty(result.GeneratedSources);
    }

    private static (GeneratorDriver Driver, Compilation Output) Generate(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location));
        var compilation = CSharpCompilation.Create("PopulationSchemaProof",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new PopulationSchemaGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));

        return (driver.RunGeneratorsAndUpdateCompilation(compilation, out var output, out _), output);
    }
}
