using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Systemic.Kernel.Generator;
using Xunit;

namespace Systemic.Kernel.Generator.Tests;

public sealed class PopulationSchemaGeneratorTests
{
    [Fact]
    public void Generates_a_typed_population_facade_for_an_annotated_partial_struct()
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
                        columns.Position.EditNext(rows.Start, rows.Length).Clear();
                        columns.Energy.EditRange(rows.Start, rows.Length).Fill(1.0f);
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
    public void Generated_row_and_range_writes_use_checked_columns_and_restore_transaction_state()
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
                    var snapshot = columns.ReadRow(1);
                    var changed = snapshot;
                    changed.X = 90;
                    changed.Health = 42;
                    columns.WriteRow(1, changed);
                    if (snapshot.X != 20 || snapshot.Health != 2) return false;
                    if (columns.ReadRow(1).X != 20 || columns.ReadRow(1).Health != 42) return false;
                    columns.X.Swap();
                    if (columns.ReadRow(1).X != 90) return false;

                    using (var transaction = world.BeginTransaction())
                    {
                        var writer = columns.WriteRange(new PopulationRange(0, 2));
                        if (writer.Length != 2) return false;
                        writer.Write(0, new ParticlePopulation.Row { X = 101, Health = 201 });
                        writer.Write(1, new ParticlePopulation.Row { X = 102, Health = 202 });
                        if (columns.ReadRow(0).X != 10 || columns.ReadRow(0).Health != 201) return false;
                        columns.X.Swap();
                        if (columns.ReadRow(0).X != 101 || columns.ReadRow(1).X != 102) return false;
                        transaction.Rollback();
                    }
                    return columns.ReadRow(0).X == 10 && columns.ReadRow(0).Health == 1
                        && columns.ReadRow(1).X == 90 && columns.ReadRow(1).Health == 42
                        && columns.ReadRow(2).X == 30 && columns.ReadRow(2).Health == 3;
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
    public void Generated_projections_reject_invalid_rows_and_ranges_before_writing()
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
                    var failures = 0;
                    try { columns.ReadRow(-1); } catch (ArgumentOutOfRangeException) { failures++; }
                    try { columns.WriteRow(2, new ParticlePopulation.Row()); } catch (ArgumentOutOfRangeException) { failures++; }
                    try { columns.WriteRange(new PopulationRange(-1, 1)); } catch (ArgumentOutOfRangeException) { failures++; }
                    try { columns.WriteRange(new PopulationRange(1, 2)); } catch (ArgumentOutOfRangeException) { failures++; }
                    var writer = columns.WriteRange(new PopulationRange(0, 2));
                    try { writer.Write(2, new ParticlePopulation.Row()); } catch (ArgumentOutOfRangeException) { failures++; }
                    var empty = columns.WriteRange(new PopulationRange(2, 0));
                    return failures == 5 && empty.Length == 0 && columns.ReadRow(0).Health == 1
                        && columns.ReadRow(1).Health == 2;
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
    public void Schemas_with_the_same_name_in_different_namespaces_generate_independently()
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
    public void Schema_namespaces_that_differ_only_by_case_do_not_collide()
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
    public void Generated_projections_support_keywords_and_fields_named_like_local_parameters()
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
                    var value = columns.ReadRow(0);
                    value.row += 10;
                    columns.WriteRow(0, value);
                    value = columns.ReadRow(0);
                    return value.row + value.range + value.population + value.@class + value.á + value.a_;
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
    public void Reports_invalid_schema_instead_of_emitting_broken_source(string declaration, string message)
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
        Assert.Contains(message, diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.GeneratedSources);
    }

    [Theory]
    [InlineData("[PopulationColumn(\"x\")] public int ReadRow;", "ReadRow")]
    [InlineData("[PopulationColumn(\"x\")] public int Row;", "Row")]
    [InlineData("[PopulationColumn(\"x\")] public int Columns;", "Columns")]
    [InlineData("[PopulationColumn(\"x\")] public int FooKey; [PopulationColumn(\"y\")] public int ReadFoo;", "ReadFooKey")]
    public void Reports_generated_member_name_conflicts(string fields, string conflictingName)
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
        Assert.Contains(conflictingName, diagnostic.GetMessage(), StringComparison.Ordinal);
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
