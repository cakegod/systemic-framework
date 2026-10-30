using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace Systemic.Tests.Cli;

public sealed class CliProcessTests
{
    [Fact]
    public async Task HelpListsTheSupportedCommandsAndReturnsSuccess()
    {
        var result = await RunCliAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("validate", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("trace", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidCommandShapeKeepsTheUsageExitCode()
    {
        var result = await RunCliAsync("validate");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage:", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContentLoadingFailureKeepsTheContentErrorExitCode()
    {
        var result = await RunCliAsync("validate", SamplePath("missing-content-pack"));

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Content error:", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CoreOnlySandboxValidatesAndTracesThroughTheCli()
    {
        var root = FindRepositoryRoot();
        var sample = Path.Combine(root, "samples", "systemic-sandbox");

        var validation = await RunCliAsync("validate", sample);
        Assert.Equal(0, validation.ExitCode);
        Assert.Contains("Valid: sample.systemic-sandbox", validation.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Gameplay2D content: none", validation.StandardOutput, StringComparison.Ordinal);

        var trace = await RunCliAsync("trace", sample, "contact", "actor.target", "actor.target");
        Assert.Equal(0, trace.ExitCode);
        Assert.Contains("contact.damage", trace.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Facts: 1; rules: 1; effects: 1; cutoff: none; rolled back: False", trace.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuarryValidatesAllDeclaredModulesThroughTheCli()
    {
        var sample = SamplePath("incremental-quarry");

        var result = await RunCliAsync("validate", sample);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Valid: sample.quarry (Quarry)", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Producers: 1; upgrades: 1; build choices: 6", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Visual recipes: 8; UI layouts: 1; presentation timelines: 2; bindings: 4",
            result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Gameplay2D actors: 2; statuses: 1", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuarryTraceRunsCrossModuleRulesThroughTheCli()
    {
        var result = await RunCliAsync("trace", SamplePath("incremental-quarry"), "incremental.production",
            "quarry.ember-source", "quarry.ore");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("quarry.ember-drill-produces-ore", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("quarry.ore-production-connects-to-ember", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Facts: 2; rules: 2; effects: 3; cutoff: none; rolled back: False", result.StandardOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatchTimeoutStopsAndAwaitsTheCliProcessTree()
    {
        var result = await RunCliAsync(TimeSpan.FromMilliseconds(250), "Watching", "watch", SamplePath("systemic-sandbox"))
            .WaitAsync(TimeSpan.FromSeconds(15));

        try
        {
            Assert.True(result.TimedOut);
            Assert.True(result.ProcessExitedAfterTimeout);
        }
        finally
        {
            if (!result.ProcessExitedAfterTimeout)
            {
                await KillProcessTreeAsync(result.ProcessId);
            }
        }
    }

    [Fact]
    public async Task NewTemplateRunsAndVerifiesWithJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"systemic-cli-{Guid.NewGuid():N}");
        try
        {
            var create = await RunCliAsync("new", directory, "--json");
            Assert.Equal(0, create.ExitCode);
            using (var document = JsonDocument.Parse(create.StandardOutput))
            {
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            }

            var run = await RunCliAsync("run", directory, "--ticks", "120", "--json");
            Assert.Equal(0, run.ExitCode);
            using (var document = JsonDocument.Parse(run.StandardOutput))
            {
                Assert.Equal(120, document.RootElement.GetProperty("tick").GetInt32());
            }

            var verify = await RunCliAsync("verify", directory, "--ticks", "120", "--json");
            Assert.Equal(0, verify.ExitCode);
            using (var document = JsonDocument.Parse(verify.StandardOutput))
            {
                Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task JsonContentErrorUsesTheMachineEnvelope()
    {
        var result = await RunCliAsync("validate", SamplePath("missing-content-pack"), "--json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("\"code\": \"content.invalid\"", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("\"ok\": false", result.StandardOutput, StringComparison.Ordinal);
    }

    private static async Task<CliResult> RunCliAsync(params string[] arguments)
    {
        return await RunCliAsync(TimeSpan.FromSeconds(30), null, arguments);
    }

    private static async Task<CliResult> RunCliAsync(TimeSpan timeout, string? readyOutput, params string[] arguments)
    {
        using var process = Process.Start(CreateStartInfo(arguments))!;
        var output = new StringBuilder();
        var error = new StringBuilder();
        var outputLock = new object();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not { } line)
            {
                return;
            }

            lock (outputLock)
            {
                output.AppendLine(line);
            }

            if (readyOutput is not null && line.Contains(readyOutput, StringComparison.Ordinal))
            {
                ready.TrySetResult();
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is { } line)
            {
                lock (outputLock)
                {
                    error.AppendLine(line);
                }
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var processId = process.Id;
        var exit = process.WaitForExitAsync();
        var timedOut = false;
        try
        {
            if (readyOutput is not null)
            {
                var startup = await Task.WhenAny(ready.Task, exit).WaitAsync(TimeSpan.FromSeconds(30));
                if (startup == exit)
                {
                    await exit;
                }
                else
                {
                    await exit.WaitAsync(timeout);
                }
            }
            else
            {
                await exit.WaitAsync(timeout);
            }
        }
        catch (TimeoutException)
        {
            timedOut = true;
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            await process.WaitForExitAsync();
        }

        process.WaitForExit();

        return new CliResult(process.HasExited ? process.ExitCode : -1, output.ToString(), error.ToString(),
            timedOut, process.HasExited, processId);
    }

    private static async Task KillProcessTreeAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private static ProcessStartInfo CreateStartInfo(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = FindRepositoryRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(Path.Combine(FindRepositoryRoot(), "src", "tools", "Systemic.Cli",
            "Systemic.Cli.csproj"));
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add(BuildConfiguration());
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string SamplePath(string name) =>
        Path.Combine(FindRepositoryRoot(), "samples", name);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Systemic.sln")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Systemic.sln from the test output directory.");
    }

    private static string BuildConfiguration() =>
        new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Parent?.Name ?? "Release";

    private sealed record CliResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut = false,
        bool ProcessExitedAfterTimeout = true,
        int ProcessId = 0);
}
