using System.Net;
using System.CommandLine;
using System.Text;
using System.Text.Json;
using Systemic.Content;
using Systemic.Gameplay2D;
using Systemic.Host;
using Systemic.Incremental;
using Systemic.Presentation;
using Systemic.Preview;
using Systemic.Rules;

if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

var parseResult = CreateRootCommand().Parse(args);
if (parseResult.Errors.Count > 0)
{
    PrintUsage();
    return 2;
}

try
{
    return await parseResult.InvokeAsync(new InvocationConfiguration
    {
        EnableDefaultExceptionHandler = false,
    });
}
catch (ContentPackException exception)
{
    Console.Error.WriteLine($"Content error: {exception.Message}");
    return 1;
}
catch (Gameplay2DContentException exception)
{
    Console.Error.WriteLine($"Gameplay2D content error: {exception.Message}");
    return 1;
}
catch (HttpListenerException exception)
{
    Console.Error.WriteLine($"Preview server error: {exception.Message}");
    return 1;
}
catch (IOException exception)
{
    Console.Error.WriteLine($"File error: {exception.Message}");
    return 1;
}
catch (ArgumentException exception)
{
    Console.Error.WriteLine($"Argument error: {exception.Message}");
    return 2;
}
catch (KeyNotFoundException exception)
{
    Console.Error.WriteLine($"Content reference error: {exception.Message}");
    return 1;
}

static RootCommand CreateRootCommand()
{
    var root = new RootCommand("Author and inspect a systemic game content pack.");
    var newDirectory = new Argument<string>("directory") { Description = "Destination directory for a new project." };
    var validateDirectory = new Argument<string>("directory") { Description = "Content pack directory to validate." };
    var watchDirectory = new Argument<string>("directory") { Description = "Content pack directory to watch." };
    var previewDirectory = new Argument<string>("directory") { Description = "Content pack directory to preview." };
    var traceDirectory = new Argument<string>("directory") { Description = "Content pack directory to trace." };
    var eventName = new Argument<string>("event") { Description = "Stable event ID to dispatch." };
    var sourceId = new Argument<string?>("source-content-id")
    {
        Arity = ArgumentArity.ZeroOrOne,
        Description = "Optional source content ID.",
    };
    var targetId = new Argument<string?>("target-content-id")
    {
        Arity = ArgumentArity.ZeroOrOne,
        Description = "Optional target content ID.",
    };
    var port = new Option<int>("--port")
    {
        Description = "Local preview server port (default: 8090).",
        DefaultValueFactory = _ => 8090,
    };
    port.Validators.Add(result =>
    {
        var value = result.GetValueOrDefault<int>();
        if (value is < 1 or > 65535)
        {
            result.AddError("Port must be between 1 and 65535.");
        }
    });

    var create = new Command("new", "Create a minimal systemic project.");
    create.Arguments.Add(newDirectory);
    create.SetAction(parse =>
    {
        CreateProject(parse.GetValue(newDirectory)!);
        return 0;
    });

    var validate = new Command("validate", "Validate a content pack.");
    validate.Arguments.Add(validateDirectory);
    validate.SetAction(parse => ValidateProject(parse.GetValue(validateDirectory)!));

    var watch = new Command("watch", "Revalidate content when files change.");
    watch.Arguments.Add(watchDirectory);
    watch.SetAction(async parse => await WatchAsync(parse.GetValue(watchDirectory)!));

    var preview = new Command("preview", "Serve a read-only local content preview.");
    preview.Arguments.Add(previewDirectory);
    preview.Options.Add(port);
    preview.SetAction(async parse => await PreviewAsync(parse.GetValue(previewDirectory)!, parse.GetValue(port)));

    var trace = new Command("trace", "Dispatch and print an event trace.");
    trace.Arguments.Add(traceDirectory);
    trace.Arguments.Add(eventName);
    trace.Arguments.Add(sourceId);
    trace.Arguments.Add(targetId);
    trace.SetAction(parse =>
    {
        var source = parse.GetValue(sourceId);
        var target = parse.GetValue(targetId);
        if ((source is null) != (target is null))
        {
            PrintUsage();
            return 2;
        }

        var traceArguments = new List<string>
        {
            "trace",
            parse.GetValue(traceDirectory)!,
            parse.GetValue(eventName)!,
        };
        if (source is not null)
        {
            traceArguments.Add(source);
            traceArguments.Add(target!);
        }

        Trace([.. traceArguments]);
        return 0;
    });

    root.Subcommands.Add(create);
    root.Subcommands.Add(validate);
    root.Subcommands.Add(watch);
    root.Subcommands.Add(preview);
    root.Subcommands.Add(trace);
    return root;
}

static int ValidateProject(string directory)
{
    var pack = LoadProject(directory);
    var incremental = pack.Modules.Select(module => module.Content).OfType<IncrementalModuleContent>().SingleOrDefault();
    var presentation = pack.Modules.Select(module => module.Content).OfType<PresentationModuleContent>().SingleOrDefault();
    var gameplay2D = pack.Modules.Select(module => module.Content).OfType<Gameplay2DContent>().SingleOrDefault();
    Console.WriteLine($"Valid: {pack.Manifest.Id} ({pack.Manifest.Name})");
    Console.WriteLine($"Entities: {pack.Entities.Definitions.Length}; rules: {pack.Rules.Rules.Length}; schedules: {pack.Rules.Schedules.Length}");
    Console.WriteLine($"Producers: {incremental?.Catalog.Producers.Length ?? 0}; upgrades: {incremental?.Catalog.Upgrades.Length ?? 0}; build choices: {incremental?.Choices.Choices.Length ?? 0}");
    Console.WriteLine($"Visual recipes: {presentation?.Visuals.Recipes.Length ?? 0}; UI layouts: {presentation?.Ui.Layouts.Length ?? 0}; presentation timelines: {presentation?.Catalog.Timelines.Length ?? 0}; bindings: {presentation?.Catalog.Bindings.Length ?? 0}");
    Console.WriteLine(gameplay2D is null
        ? "Gameplay2D content: none"
        : $"Gameplay2D actors: {gameplay2D.Actors.Count}; statuses: {gameplay2D.Statuses.Count}");
    return 0;
}

static async Task<int> PreviewAsync(string directory, int port)
{

    var pack = LoadProject(directory);

    using var cancellation = CreateConsoleCancellation();
    Console.WriteLine($"Read-only preview for {pack.Manifest.Id}: http://127.0.0.1:{port}/ (Ctrl+C to stop)");
    await ContentPreviewServer.RunAsync(pack, port, cancellation.Token);
    return 0;
}

static async Task<int> WatchAsync(string root)
{
    var fullPath = Path.GetFullPath(root);
    _ = LoadProject(fullPath);
    using var cancellation = CreateConsoleCancellation();
    using var changes = new SemaphoreSlim(0, 1);
    using var watcher = new FileSystemWatcher(fullPath, "*.json")
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        EnableRaisingEvents = true,
    };
    void changed(object _1, FileSystemEventArgs _2) => SignalChange();
    void renamed(object _1, RenamedEventArgs _2) => SignalChange();
    watcher.Changed += changed;
    watcher.Created += changed;
    watcher.Deleted += changed;
    watcher.Renamed += renamed;

    Console.WriteLine($"Watching {fullPath} for content changes (Ctrl+C to stop)");
    while (!cancellation.IsCancellationRequested)
    {
        try
        {
            await changes.WaitAsync(cancellation.Token);
            await Task.Delay(100, cancellation.Token);
            while (changes.Wait(0)) { }

            try
            {
                var pack = LoadProject(fullPath);
                var gameplay2D = pack.Modules.Select(module => module.Content).OfType<Gameplay2DContent>().SingleOrDefault();
                Console.WriteLine($"Valid: {pack.Manifest.Id} ({pack.Manifest.Name})");
                if (gameplay2D is not null)
                {
                    Console.WriteLine($"Gameplay2D: {gameplay2D.Actors.Count} actors; {gameplay2D.Statuses.Count} statuses");
                }
            }
            catch (Exception exception) when (exception is ContentPackException or Gameplay2DContentException)
            {
                Console.Error.WriteLine($"Invalid: {exception.Message}");
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            break;
        }
    }

    return 0;

    void SignalChange()
    {
        if (changes.CurrentCount == 0)
        {
            try
            {
                changes.Release();
            }
            catch (SemaphoreFullException)
            {
                // Duplicate filesystem events collapse into one validation pass.
            }
        }
    }
}

static void Trace(string[] arguments)
{
    var content = LoadProject(arguments[1]);
    var materialized = content.CreateWorld(seed: 1, valueScale: 1);
    var game = new SystemicGameBuilder(content, materialized).Build();
    var sourceId = arguments.Length == 5 ? game.EntityId(arguments[3]) : 0;
    var targetId = arguments.Length == 5 ? game.EntityId(arguments[4]) : 0;
    var trace = game.Trace(new SimulationFact(arguments[2], sourceId, targetId));
    string ContentName(int id) => game.EntityIds.FirstOrDefault(pair => pair.Value == id).Key ?? $"entity:{id}";

    foreach (var entry in trace.Facts)
    {
        var rules = entry.MatchedRules.IsEmpty ? "-" : string.Join(", ", entry.MatchedRules);
        Console.WriteLine($"[{entry.Sequence}] depth={entry.Fact.Depth} {entry.Fact.Type} ({ContentName(entry.Fact.SourceId)} -> {ContentName(entry.Fact.TargetId)}) rules: {rules}");
    }

    var result = trace.Result;
    Console.WriteLine($"Facts: {result.FactsProcessed}; rules: {result.RulesMatched}; effects: {result.EffectsApplied}; cutoff: {result.CutoffReason ?? "none"}; rolled back: {result.RolledBack}");
}

static ContentPack LoadProject(string directory)
{
    var fullPath = Path.GetFullPath(directory);
    var registry = new ContentModuleRegistry()
        .Add(new Gameplay2DContentModule())
        .Add(new IncrementalContentModule())
        .Add(new PresentationContentModule());
    return ContentPackLoader.Load(fullPath, registry);
}

static void CreateProject(string destination)
{
    var fullPath = Path.GetFullPath(destination);
    if (File.Exists(fullPath))
    {
        throw new IOException($"Project path is a file: {fullPath}");
    }

    if (Directory.Exists(fullPath) && Directory.EnumerateFileSystemEntries(fullPath).Any())
    {
        throw new IOException($"Project directory is not empty: {fullPath}");
    }

    var directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
    var slugBuilder = new StringBuilder(directoryName.Length);
    var needsSeparator = false;
    foreach (var character in directoryName.ToLowerInvariant())
    {
        if (character is >= 'a' and <= 'z' or >= '0' and <= '9')
        {
            if (needsSeparator && slugBuilder.Length > 0)
            {
                slugBuilder.Append('-');
            }

            slugBuilder.Append(character);
            needsSeparator = false;
        }
        else
        {
            needsSeparator = true;
        }
    }

    var slug = slugBuilder.Length == 0 ? "game" : slugBuilder.ToString();

    var name = JsonSerializer.Serialize(directoryName);
    var id = JsonSerializer.Serialize($"sample.{slug}");
    Directory.CreateDirectory(fullPath);
    Write("game.json", $$"""
        {
          "schemaVersion": 3,
          "id": {{id}},
          "name": {{name}},
          "tickRateHz": 60,
          "maxCatchUpSteps": 4,
          "rules": "rules.json",
          "entities": "entities.json"
        }
        """);
    Write("rules.json", "{\"schemaVersion\":2,\"rules\":[]}\n");
    Write("entities.json", """
        { "schemaVersion": 2, "entities": [
          { "id": "resource.energy", "kind": "resource", "tags": ["resource"], "properties": { "balance": 0 } },
          { "id": "actor.generator", "kind": "actor", "properties": { "rate": 1 }, "visualRecipeId": "generator" }
        ] }
        """ + "\n");
    Console.WriteLine($"Created systemic game project: {fullPath}");
    Console.WriteLine("Next: systemic validate " + fullPath);

    void Write(string file, string contents) => File.WriteAllText(Path.Combine(fullPath, file), contents);
}

static CancellationTokenSource CreateConsoleCancellation()
{
    var cancellation = new CancellationTokenSource();
    void handler(object? _, ConsoleCancelEventArgs eventArgs)
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    }
    Console.CancelKeyPress += handler;
    cancellation.Token.Register(() => Console.CancelKeyPress -= handler);
    return cancellation;
}

static void PrintUsage() =>
    Console.WriteLine("Usage: systemic <new <directory>|validate <directory>|watch <directory>|preview <directory> [--port <1-65535>]|trace <directory> <event> [source-content-id target-content-id]>");
