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
