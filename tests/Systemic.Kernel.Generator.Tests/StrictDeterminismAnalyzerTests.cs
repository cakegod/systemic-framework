using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Systemic.Kernel.Generator;
using Xunit;

namespace Systemic.Kernel.Generator.Tests;

public sealed class StrictDeterminismAnalyzerTests
{
    [Fact]
    public void Diagnostics_are_errors_and_link_to_the_matching_guide_anchor()
    {
        var diagnostics = new StrictDeterminismAnalyzer().SupportedDiagnostics;

        Assert.Equal(3, diagnostics.Length);
        foreach (var descriptor in diagnostics)
        {
            Assert.Equal("Determinism", descriptor.Category);
            Assert.Equal(DiagnosticSeverity.Error, descriptor.DefaultSeverity);
            Assert.True(descriptor.IsEnabledByDefault);
            Assert.Equal(
                $"https://github.com/cakegod/systemic-framework/blob/main/docs/api/analyzers.md#{descriptor.Id.ToLowerInvariant()}",
                descriptor.HelpLinkUri);
        }
    }

    [Fact]
    public async Task Strict_system_reports_floating_math_clock_random_parallel_and_unordered_iteration()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using Systemic.Kernel;

            [StrictSimulation]
            internal sealed class Simulation
            {
                private float _speed;
                public void Step()
                {
                    _speed = MathF.Sin(_speed);
                    var wall = DateTime.UtcNow;
                    var random = new Random();
                    Parallel.For(0, 3, i => { });
                    var ids = new HashSet<int> { 1, 2 };
                    foreach (var id in ids) { _ = id; }
                }

                [CosmeticOnly]
                public void Draw()
                {
                    float alpha = MathF.Sin(0.5f);
                    _ = DateTime.Now;
                }
            }

            internal sealed class NotStrict
            {
                public double Render() => Math.Sin(1.0);
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET001" && At(d, source).Contains("float"));
        Assert.Contains(diagnostics, d => d.Id == "SYSDET001" && At(d, source).Contains("MathF.Sin"));
        Assert.Contains(diagnostics, d => d.Id == "SYSDET002" && At(d, source).Contains("DateTime.UtcNow"));
        Assert.Contains(diagnostics, d => d.Id == "SYSDET002" && At(d, source).Contains("new Random"));
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source).Contains("Parallel.For"));
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source).Contains("ids"));
        var cosmeticStart = source.IndexOf("[CosmeticOnly]", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Location.SourceSpan.Start >= cosmeticStart);
    }

    [Fact]
    public async Task File_configuration_can_enable_strict_analysis_and_cosmetic_type_can_opt_out()
    {
        const string source = """
            using System;
            using Systemic.Kernel;
            internal class SystemLogic
            {
                public void Step() { double value = 1.5; _ = value; }
            }
            [CosmeticOnly]
            internal class Visuals
            {
                public void Draw() { float value = 0.5f; _ = DateTime.Now; }
            }
            """;
        var unconfigured = await Analyze(source);
        Assert.Empty(unconfigured);
        var diagnostics = await Analyze(source, fileMode: "strict");
        Assert.Contains(diagnostics, d => d.Id == "SYSDET001" && At(d, source).Contains("double"));
        var cosmeticStart = source.IndexOf("[CosmeticOnly]", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Location.SourceSpan.Start >= cosmeticStart);
    }

    [Fact]
    public async Task Gameplay_system_implementations_are_strict_by_default()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class HostSystem : ISystemicSystem
            {
                public string Id => "host";
                public int Order => 0;
                public void Step(SystemicSystemContext context) { _ = DateTime.UtcNow; }
            }
            internal sealed class HostScheduledSystem : ISystemicSystem
            {
                public string Id => "host-scheduled";
                public int Order => 1;
                public IReadOnlyList<SystemAccess> Access => Array.Empty<SystemAccess>();
                public void Step(SystemicSystemContext context) { _ = DateTime.UtcNow; }
            }
            internal sealed class KernelScheduledSystem : IScheduledKernelSystem
            {
                public string Id => "kernel";
                public IReadOnlyList<SystemAccess> Access => Array.Empty<SystemAccess>();
                public void Execute(ScheduledSystemContext context) { _ = DateTime.UtcNow; }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Equal(3, diagnostics.Count(d => d.Id == "SYSDET002" && At(d, source).Contains("DateTime.UtcNow")));
    }

    [Fact]
    public async Task Unrelated_interface_with_gameplay_name_does_not_enable_default_strict_analysis()
    {
        const string source = """
            using System;
            namespace Systemic.Host { internal interface ISystemicSystem { void Step(); } }
            internal sealed class UnrelatedSystem : Systemic.Host.ISystemicSystem
            {
                public void Step() { _ = DateTime.UtcNow; }
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Strict_code_reports_floating_point_results_from_inline_calls()
    {
        const string source = """
            using Systemic.Kernel;
            using System.Numerics;
            internal static class ExternalPhysics
            {
                public static Vector2 NextPosition() => default;
            }
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step() { _ = ExternalPhysics.NextPosition(); }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET001" && At(d, source) == "ExternalPhysics.NextPosition()");
    }

    [Fact]
    public async Task Strict_code_reports_composite_floating_point_call_results()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Numerics;
            using Systemic.Kernel;
            internal static class ExternalPhysics
            {
                public static float[] NextSamples() => [];
                public static List<Vector2> NextPositions() => [];
                public static Complex NextComplex() => Complex.Zero;
            }
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    _ = ExternalPhysics.NextSamples();
                    _ = ExternalPhysics.NextPositions();
                    _ = ExternalPhysics.NextComplex();
                }
            }
            """;

        var diagnostics = await Analyze(source);
        foreach (var call in new[] { "NextSamples()", "NextPositions()", "NextComplex()" })
            Assert.Contains(diagnostics, d => d.Id == "SYSDET001" && At(d, source).Contains(call));
    }

    [Fact]
    public async Task Strict_code_reports_task_start_and_continuation_scheduling()
    {
        const string source = """
            using System.Threading.Tasks;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step(Task existing)
                {
                    var task = new Task(() => { });
                    task.Start();
                    _ = existing.ContinueWith(_ => { });
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source) == "task.Start()");
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source) == "existing.ContinueWith(_ => { })");
    }

    [Fact]
    public async Task Strict_code_reports_unordered_sources_hidden_by_linq_and_materialization()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var values = new HashSet<int> { 1, 2 };
                    foreach (var value in values.Where(value => value > 0).Select(value => value)) { _ = value; }
                    foreach (var value in values.ToArray()) { _ = value; }
                    var orderedValues = new List<int> { 1, 2 };
                    foreach (var value in orderedValues.Where(value => value > 0).OrderBy(value => value).ToList()) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Equal(2, diagnostics.Count(d => d.Id == "SYSDET003"));
        Assert.Contains(diagnostics, d => At(d, source).Contains("values.Where(value => value > 0).Select"));
        Assert.Contains(diagnostics, d => At(d, source).Contains("values.ToArray()"));
    }

    [Fact]
    public async Task Strict_code_reports_unordered_sources_in_static_linq_calls()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var values = new HashSet<int> { 1, 2 };
                    foreach (var value in Enumerable.ToArray(Enumerable.Where(values, value => value > 0))) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source).Contains("Enumerable.ToArray"));
    }

    [Fact]
    public async Task Strict_code_reports_unordered_secondary_linq_sources()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var ordered = new List<int> { 1, 2 };
                    var unordered = new HashSet<int> { 1, 2 };
                    foreach (var value in ordered.Concat(unordered)) { _ = value; }
                    foreach (var value in Enumerable.Zip(ordered, unordered)) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Equal(2, diagnostics.Count(d => d.Id == "SYSDET003"));
        Assert.Contains(diagnostics, d => At(d, source).Contains("Concat(unordered)"));
        Assert.Contains(diagnostics, d => At(d, source).Contains("Enumerable.Zip(ordered, unordered)"));
    }

    [Fact]
    public async Task Strict_code_does_not_treat_tied_order_by_keys_as_deterministic()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var values = new HashSet<int> { 1, 2 };
                    foreach (var value in values.OrderBy(_ => 0)) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source).Contains("values.OrderBy(_ => 0)"));
    }

    [Fact]
    public async Task Strict_code_accepts_identity_ordering_of_unique_integer_set_values()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var values = new HashSet<int> { 9, 2, 5 };
                    foreach (var value in values.OrderBy(value => value)) { _ = value; }
                }
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Strict_code_rejects_default_string_ordering_of_set_values()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var values = new HashSet<string> { "a", "b" };
                    foreach (var value in values.OrderBy(value => value)) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source).Contains("values.OrderBy(value => value)"));
    }

    [Fact]
    public async Task Strict_code_rejects_an_explicit_ordering_comparer_for_set_values()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var values = new HashSet<int> { 1, 2 };
                    foreach (var value in values.OrderBy(value => value, Comparer<int>.Create((left, right) => left.CompareTo(right)))) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source).Contains("values.OrderBy"));
    }

    [Fact]
    public async Task Strict_code_rejects_hash_sets_constructed_with_an_explicit_equality_comparer()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var values = new HashSet<int>(EqualityComparer<int>.Create((left, right) => left == right, value => value)) { 1, 2 };
                    foreach (var value in values.OrderBy(value => value)) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source).Contains("values.OrderBy"));
    }

    [Fact]
    public async Task Strict_code_tracks_unordered_source_through_local_materialization()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var values = new HashSet<int> { 1, 2 };
                    var materialized = values.ToArray();
                    foreach (var value in materialized) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source) == "materialized");
    }

    [Fact]
    public async Task Strict_code_tracks_unordered_reassignment_before_iteration()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    IEnumerable<int> values = new List<int> { 1, 2 };
                    values = new HashSet<int> { 1, 2 };
                    foreach (var value in values) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source) == "values");
    }

    [Fact]
    public async Task Strict_code_ignores_unordered_assignment_overwritten_before_iteration()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    IEnumerable<int> values = new List<int> { 1, 2 };
                    values = new HashSet<int> { 1, 2 };
                    values = new List<int> { 3, 4 };
                    foreach (var value in values) { _ = value; }
                }
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Strict_code_reports_unordered_later_query_sources()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var ordered = new List<int> { 1, 2 };
                    var unordered = new HashSet<int> { 1, 2 };
                    foreach (var value in from item in ordered join candidate in unordered on item equals candidate select candidate) { _ = value; }
                    foreach (var value in from item in ordered from candidate in unordered select candidate) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Equal(2, diagnostics.Length);
        Assert.Contains(diagnostics, d => At(d, source).Contains("join candidate in unordered"));
        Assert.Contains(diagnostics, d => At(d, source).Contains("from candidate in unordered"));
    }

    [Fact]
    public async Task Strict_code_reports_unordered_query_syntax_but_accepts_ordered_queries()
    {
        const string source = """
            using System.Collections.Generic;
            using System.Linq;
            using Systemic.Kernel;
            [StrictSimulation]
            internal sealed class Simulation
            {
                public void Step()
                {
                    var values = new HashSet<int> { 1, 2 };
                    foreach (var value in from item in values where item > 0 select item) { _ = value; }
                    var orderedValues = new List<int> { 1, 2 };
                    foreach (var value in from item in orderedValues orderby item select item) { _ = value; }
                }
            }
            """;

        var diagnostics = await Analyze(source);
        Assert.Single(diagnostics);
        Assert.Contains("from item in values", At(diagnostics[0], source));
    }

    [Fact]
    public async Task Relaxed_file_disables_gameplay_system_default_but_strict_marker_overrides_it()
    {
        const string source = """
            using System;
            using Systemic.Host;
            using Systemic.Kernel;
            internal sealed class DefaultSystem : ISystemicSystem
            {
                public string Id => "default";
                public int Order => 0;
                public void Step(SystemicSystemContext context) { _ = DateTime.UtcNow; }
            }
            [StrictSimulation] internal sealed class ExplicitlyStrictSystem : ISystemicSystem
            {
                public string Id => "explicit";
                public int Order => 0;
                public void Step(SystemicSystemContext context) { _ = DateTime.Now; }
            }
            """;

        var diagnostics = await Analyze(source, fileMode: "relaxed");
        Assert.Single(diagnostics);
        Assert.Equal("SYSDET002", diagnostics[0].Id);
        Assert.Contains("DateTime.Now", At(diagnostics[0], source));
    }

    [Fact]
    public async Task Cosmetic_gameplay_system_is_excluded_from_default_strict_analysis()
    {
        const string source = """
            using System;
            using Systemic.Host;
            using Systemic.Kernel;
            [CosmeticOnly]
            internal sealed class CosmeticSystem : ISystemicSystem
            {
                public string Id => "cosmetic";
                public int Order => 0;
                public void Step(SystemicSystemContext context) { float opacity = 0.5f; _ = opacity; _ = DateTime.UtcNow; }
            }
            """;

        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Strict_integer_decimal_and_counter_based_random_are_accepted()
    {
        const string source = """
            using System;
            using System.Linq;
            using System.Collections.Generic;
            using Systemic.Kernel;
            [StrictSimulation]
            internal class Simulation
            {
                public int Step()
                {
                    var value = DeterministicDecimal.Multiply(1.2m, 2m, 2);
                    var position = (Fixed64)3;
                    var random = new DeterministicRandom(1, "movement", 2, 3).At(4);
                    var ids = new List<int> { 1, 2 };
                    foreach (var id in ids.OrderBy(id => id)) { _ = id; }
                    return Math.Abs(-1) + (int)position + (int)(random % 2) + (int)value;
                }
            }
            """;
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Diagnostics_use_symbol_identity_for_clock_random_and_parallel_apis()
    {
        const string source = """
            using Systemic.Kernel;
            namespace Example;
            public sealed class Random { public int Next() => 1; }
            public static class Parallel { public static void For() { } }
            [StrictSimulation] internal sealed class System
            {
                public void Step() { var random = new Random(); _ = random.Next(); Parallel.For(); }
            }
            """;
        Assert.Empty(await Analyze(source));
    }

    [Fact]
    public async Task Assembly_and_method_markers_apply_to_their_own_scopes()
    {
        const string source = """
            using System;
            using Systemic.Kernel;
            [assembly: StrictSimulation]
            internal class SystemLogic
            {
                public void Step() { _ = Guid.NewGuid(); }
                [CosmeticOnly] public void Draw() { _ = DateTime.UtcNow; }
            }
            """;
        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET002" && At(d, source).Contains("Guid.NewGuid"));
        var cosmeticStart = source.IndexOf("[CosmeticOnly]", StringComparison.Ordinal);
        Assert.DoesNotContain(diagnostics, d => d.Location.SourceSpan.Start >= cosmeticStart);
    }

    [Fact]
    public async Task Strict_code_detects_floating_values_referenced_from_an_external_scope()
    {
        const string source = """
            using System;
            using Real = System.Double;
            using Systemic.Kernel;
            internal static class External
            {
                public static double Field;
                public static float Property => 0.5f;
                public static void Consume(double value) { }
            }
            [StrictSimulation] internal sealed class Simulation
            {
                Real _value;
                public void Step()
                {
                    External.Consume(External.Field);
                    Console.WriteLine(External.Property);
                }
            }
            """;
        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET001" && At(d, source) == "Real");
        Assert.Contains(diagnostics, d => d.Id == "SYSDET001" && At(d, source).Contains("External.Field"));
        Assert.Contains(diagnostics, d => d.Id == "SYSDET001" && At(d, source).Contains("External.Property"));
    }

    [Fact]
    public async Task Strict_code_flags_clock_crypto_tasks_and_dictionary_key_iteration()
    {
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Diagnostics;
            using System.Security.Cryptography;
            using System.Threading.Tasks;
            using Systemic.Kernel;
            [StrictSimulation] internal sealed class Simulation
            {
                public void Step()
                {
                    _ = DateTimeOffset.UtcNow;
                    _ = Environment.TickCount64;
                    _ = Stopwatch.GetTimestamp();
                    _ = RandomNumberGenerator.GetInt32(100);
                    _ = Task.Run(() => 1);
                    var dictionary = new Dictionary<int, int>();
                    foreach (var key in dictionary.Keys) { _ = key; }
                }
            }
            """;
        var diagnostics = await Analyze(source);
        foreach (var expression in new[] { "DateTimeOffset.UtcNow", "Environment.TickCount64", "Stopwatch.GetTimestamp", "RandomNumberGenerator.GetInt32" })
            Assert.Contains(diagnostics, d => d.Id == "SYSDET002" && At(d, source).Contains(expression));
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source).Contains("Task.Run"));
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source).Contains("dictionary.Keys"));
    }

    [Fact]
    public async Task Existing_marker_is_reused_and_strict_method_does_not_cover_unmarked_siblings()
    {
        const string source = """
            using System;
            using Systemic.Kernel;
            namespace Systemic.Kernel
            {
                [AttributeUsage(AttributeTargets.Method)]
                internal sealed class StrictSimulationAttribute : Attribute { }
            }
            internal sealed class Mixed
            {
                [StrictSimulation] public void Update() { _ = DateTime.UtcNow; }
                public void Draw() { _ = DateTime.Now; }
            }
            """;
        var diagnostics = await Analyze(source);
        Assert.Single(diagnostics);
        Assert.Equal("SYSDET002", diagnostics[0].Id);
        Assert.Contains("DateTime.UtcNow", At(diagnostics[0], source));
    }

    [Fact]
    public async Task Strict_code_flags_interfaces_that_do_not_guarantee_iteration_order()
    {
        const string source = """
            using System.Collections.Generic;
            using Systemic.Kernel;
            [StrictSimulation] internal sealed class Simulation
            {
                public void Step(IReadOnlyDictionary<int, int> dictionary, IReadOnlySet<int> set)
                {
                    foreach (var pair in dictionary) { _ = pair; }
                    foreach (var item in set) { _ = item; }
                }
            }
            """;
        var diagnostics = await Analyze(source);
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source) == "dictionary");
        Assert.Contains(diagnostics, d => d.Id == "SYSDET003" && At(d, source) == "set");
    }

    private static string At(Diagnostic diagnostic, string source) => source.Substring(
        diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length);

    private static async Task<ImmutableArray<Diagnostic>> Analyze(string source, string? fileMode = null)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(World).Assembly.Location))
            .Append(MetadataReference.CreateFromFile(typeof(Systemic.Host.SystemicSystemContext).Assembly.Location));
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), path: "simulation.cs");
        var compilation = CSharpCompilation.Create("StrictProof", [tree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [new DeterminismMarkerGenerator().AsSourceGenerator()],
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview));
        driver.RunGeneratorsAndUpdateCompilation(compilation, out var generated, out var generatorDiagnostics);
        Assert.Empty(generatorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Empty(generated.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
        var options = new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty, new ConfigProvider(fileMode));
        return await generated.WithAnalyzers([new StrictDeterminismAnalyzer()], options).GetAnalyzerDiagnosticsAsync();
    }

    private sealed class ConfigProvider(string? fileMode) : AnalyzerConfigOptionsProvider
    {
        private readonly AnalyzerConfigOptions _tree = new Config(fileMode);
        private readonly AnalyzerConfigOptions _empty = new Config(null);
        public override AnalyzerConfigOptions GlobalOptions => _empty;
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => _tree;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => _empty;
    }

    private sealed class Config(string? mode) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            if (mode is not null && key == "systemic_determinism") { value = mode; return true; }
            value = string.Empty;
            return false;
        }
    }
}
