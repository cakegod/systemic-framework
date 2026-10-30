using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;
using Systemic.Content;
using Systemic.Gameplay2D;
using Systemic.Host;
using Systemic.Incremental;
using Systemic.Kernel;
using Systemic.Presentation;
using Systemic.Replay;
using Systemic.Rules;
using Systemic.Save;

var jsonRequested = args.Contains("--json", StringComparer.Ordinal);
if (args.Length == 0)
{
    PrintUsage();
    return 2;
}

var parseResult = CreateRootCommand().Parse(args);
if (parseResult.Errors.Count > 0)
{
    if (jsonRequested)
    {
        WriteError("usage.invalid", string.Join("; ", parseResult.Errors.Select(error => error.Message)));
    }
    else
    {
        PrintUsage();
    }

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
    return Fail("content.invalid", exception.Message, 1, exception.Diagnostics.Select(diagnostic => new
    {
        diagnostic.Code,
        diagnostic.File,
        diagnostic.Path,
        diagnostic.Message,
        diagnostic.Line,
        diagnostic.Column,
    }));
}
catch (Gameplay2DContentException exception)
{
    return Fail("content.gameplay2d", exception.Message, 1);
}
catch (SystemicSaveException exception)
{
    return Fail("save.invalid", exception.Message, 1);
}
catch (IOException exception)
{
    return Fail("io.error", exception.Message, 1);
}
catch (ArgumentException exception)
{
    return Fail("usage.invalid", exception.Message, 2);
}
catch (KeyNotFoundException exception)
{
    return Fail("content.reference", exception.Message, 1);
}
catch (Exception exception)
{
    return Fail("runtime.error", exception.Message, 3);
}

static RootCommand CreateRootCommand()
{
    var root = new RootCommand("Author, run, and verify a systemic game content pack.");

    var newDirectory = new Argument<string>("directory") { Description = "Destination directory for a new project." };
    var validateDirectory = new Argument<string>("directory") { Description = "Content pack directory to validate." };
    var watchDirectory = new Argument<string>("directory") { Description = "Content pack directory to watch." };
    var traceDirectory = new Argument<string>("directory") { Description = "Content pack directory to trace." };
    var runDirectory = new Argument<string>("directory") { Description = "Content pack directory to run." };
    var simulateDirectory = new Argument<string>("directory") { Description = "Content pack directory to simulate." };
    var dumpDirectory = new Argument<string>("directory") { Description = "Content pack directory to dump." };
    var saveDirectory = new Argument<string>("directory") { Description = "Content pack directory to save." };
    var loadDirectory = new Argument<string>("directory") { Description = "Content pack directory to load." };
    var verifyDirectory = new Argument<string>("directory") { Description = "Content pack directory to verify." };
    var replayDirectory = new Argument<string>("directory") { Description = "Content pack directory for the replay." };
    var assetsDirectory = new Argument<string>("directory") { Description = "Content pack directory containing visual recipes." };
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
    var template = new Option<string>("--template")
    {
        Description = "Template name (default: systemic).",
        DefaultValueFactory = _ => "systemic",
    };
    var ticks = new Option<int>("--ticks")
    {
        Description = "Number of ticks to run (default: 600).",
        DefaultValueFactory = _ => 600,
    };
    var seed = new Option<int>("--seed")
    {
        Description = "World seed (default: 1).",
        DefaultValueFactory = _ => 1,
    };
    var seconds = new Option<double>("--seconds")
    {
        Description = "Seconds of game time to simulate (default: 3600).",
        DefaultValueFactory = _ => 3600,
    };
    var csv = new Option<string>("--csv")
    {
        Description = "CSV output path (default: artifacts/simulate.csv).",
        DefaultValueFactory = _ => Path.Combine("artifacts", "simulate.csv"),
    };
    var output = new Option<string>("--out")
    {
        Description = "Output file path (default: artifacts/run.json).",
        DefaultValueFactory = _ => Path.Combine("artifacts", "run.json"),
    };
    var savePath = new Option<string>("--save")
    {
        Description = "Save or replay file path (default: artifacts/run.json).",
        DefaultValueFactory = _ => Path.Combine("artifacts", "run.json"),
    };
    var scale = new Option<int>("--scale")
    {
        Description = "Contact sheet integer scale (default: 4).",
        DefaultValueFactory = _ => 4,
    };
    var manifest = new Option<string>("--manifest")
    {
        Description = "Atlas manifest path (default: artifacts/atlas.json).",
        DefaultValueFactory = _ => Path.Combine("artifacts", "atlas.json"),
    };

    var create = new Command("new", "Create a playable systemic project.");
    create.Arguments.Add(newDirectory);
    create.Options.Add(template);
    AddJsonOption(create);
    create.SetAction(parse =>
    {
        CreateProject(parse.GetValue(newDirectory)!, parse.GetValue(template)!, IsJson(parse));
        return 0;
    });

    var validate = new Command("validate", "Validate a content pack.");
    validate.Arguments.Add(validateDirectory);
    AddJsonOption(validate);
    validate.SetAction(parse => ValidateProject(parse.GetValue(validateDirectory)!, IsJson(parse)));

    var run = new Command("run", "Run a content pack headless.");
    run.Arguments.Add(runDirectory);
    run.Options.Add(ticks);
    run.Options.Add(seed);
    AddJsonOption(run);
    run.SetAction(parse => RunProject(parse.GetValue(runDirectory)!, parse.GetValue(ticks),
        parse.GetValue(seed), IsJson(parse)));

    var simulate = new Command("simulate", "Fast-forward a content pack and write a CSV.");
    simulate.Arguments.Add(simulateDirectory);
    simulate.Options.Add(seconds);
    simulate.Options.Add(seed);
    simulate.Options.Add(csv);
    AddJsonOption(simulate);
    simulate.SetAction(parse => SimulateProject(parse.GetValue(simulateDirectory)!, parse.GetValue(seconds),
        parse.GetValue(seed), parse.GetValue(csv)!, IsJson(parse)));

    var dump = new Command("dump", "Print a content pack's entities and state hash.");
    dump.Arguments.Add(dumpDirectory);
    AddJsonOption(dump);
    dump.SetAction(parse => DumpProject(parse.GetValue(dumpDirectory)!, IsJson(parse)));

    var save = new Command("save", "Run a pack and write a save.");
    save.Arguments.Add(saveDirectory);
    save.Options.Add(output);
    save.Options.Add(ticks);
    save.Options.Add(seed);
    AddJsonOption(save);
    save.SetAction(parse => SaveProject(parse.GetValue(saveDirectory)!, parse.GetValue(output)!,
        parse.GetValue(ticks), parse.GetValue(seed), IsJson(parse)));

    var load = new Command("load", "Load a save and print its state.");
    load.Arguments.Add(loadDirectory);
    load.Options.Add(savePath);
    AddJsonOption(load);
    load.SetAction(parse => LoadProject(parse.GetValue(loadDirectory)!, parse.GetValue(savePath)!,
        IsJson(parse)));

    var verify = new Command("verify", "Run twice, save and restore, and compare state hashes.");
    verify.Arguments.Add(verifyDirectory);
    verify.Options.Add(ticks);
    verify.Options.Add(seed);
    AddJsonOption(verify);
    verify.SetAction(parse => VerifyProject(parse.GetValue(verifyDirectory)!, parse.GetValue(ticks),
        parse.GetValue(seed), IsJson(parse)));

    var replay = new Command("replay", "Record or verify a deterministic replay.");
    var replayRecord = new Command("record", "Record a replay with no inputs.");
    replayRecord.Arguments.Add(replayDirectory);
    replayRecord.Options.Add(output);
    replayRecord.Options.Add(ticks);
    replayRecord.Options.Add(seed);
    AddJsonOption(replayRecord);
    replayRecord.SetAction(parse => ReplayRecord(parse.GetValue(replayDirectory)!, parse.GetValue(output)!,
        parse.GetValue(ticks), parse.GetValue(seed), IsJson(parse)));
    var replayVerify = new Command("verify", "Verify a replay.");
    replayVerify.Arguments.Add(replayDirectory);
    replayVerify.Options.Add(savePath);
    AddJsonOption(replayVerify);
    replayVerify.SetAction(parse => ReplayVerify(parse.GetValue(replayDirectory)!, parse.GetValue(savePath)!,
        IsJson(parse)));
    replay.Subcommands.Add(replayRecord);
    replay.Subcommands.Add(replayVerify);

    var assets = new Command("assets", "Render and validate procedural visual recipes.");
    var assetsSheet = new Command("sheet", "Render a labeled contact sheet.");
    assetsSheet.Arguments.Add(assetsDirectory);
    assetsSheet.Options.Add(output);
    assetsSheet.Options.Add(scale);
    AddJsonOption(assetsSheet);
    assetsSheet.SetAction(parse => AssetsSheet(parse.GetValue(assetsDirectory)!, parse.GetValue(output)!,
        parse.GetValue(scale), IsJson(parse)));
    var assetsValidate = new Command("validate", "Validate every procedural visual recipe.");
    assetsValidate.Arguments.Add(assetsDirectory);
    AddJsonOption(assetsValidate);
    assetsValidate.SetAction(parse => AssetsValidate(parse.GetValue(assetsDirectory)!, IsJson(parse)));
    var assetsAtlas = new Command("atlas", "Pack every recipe into an atlas.");
    assetsAtlas.Arguments.Add(assetsDirectory);
    assetsAtlas.Options.Add(output);
    assetsAtlas.Options.Add(manifest);
    AddJsonOption(assetsAtlas);
    assetsAtlas.SetAction(parse => AssetsAtlas(parse.GetValue(assetsDirectory)!, parse.GetValue(output)!,
        parse.GetValue(manifest)!, IsJson(parse)));
    assets.Subcommands.Add(assetsSheet);
    assets.Subcommands.Add(assetsValidate);
    assets.Subcommands.Add(assetsAtlas);

    var watch = new Command("watch", "Revalidate content when files change.");
    watch.Arguments.Add(watchDirectory);
    watch.SetAction(async parse => await WatchAsync(parse.GetValue(watchDirectory)!));

    var trace = new Command("trace", "Dispatch and print an event trace.");
    trace.Arguments.Add(traceDirectory);
    trace.Arguments.Add(eventName);
    trace.Arguments.Add(sourceId);
    trace.Arguments.Add(targetId);
    AddJsonOption(trace);
    trace.SetAction(parse =>
    {
        var source = parse.GetValue(sourceId);
        var target = parse.GetValue(targetId);
        if ((source is null) != (target is null))
        {
            throw new ArgumentException("Trace requires both source and target content IDs, or neither.");
        }

        return Trace(parse.GetValue(traceDirectory)!, parse.GetValue(eventName)!, source, target, IsJson(parse));
    });

    root.Subcommands.Add(create);
    root.Subcommands.Add(validate);
    root.Subcommands.Add(run);
    root.Subcommands.Add(simulate);
    root.Subcommands.Add(dump);
    root.Subcommands.Add(save);
    root.Subcommands.Add(load);
    root.Subcommands.Add(verify);
    root.Subcommands.Add(replay);
    root.Subcommands.Add(assets);
    root.Subcommands.Add(watch);
    root.Subcommands.Add(trace);
    return root;
}

static void AddJsonOption(Command command) => command.Options.Add(new Option<bool>("--json")
{
    Description = "Emit machine-readable JSON.",
});

static bool IsJson(ParseResult parse) => parse.GetValue<bool>("--json");

static int ValidateProject(string directory, bool json)
{
    var pack = LoadPack(directory);
    var incremental = pack.Modules.Select(module => module.Content).OfType<IncrementalModuleContent>().SingleOrDefault();
    var presentation = pack.Modules.Select(module => module.Content).OfType<PresentationModuleContent>().SingleOrDefault();
    var gameplay2D = pack.Modules.Select(module => module.Content).OfType<Gameplay2DContent>().SingleOrDefault();
    // The state hash needs a live host, and a command that owns one must stop its workers before it returns.
    using var host = HeadlessSystemicHost.Create(pack, 1);
    var result = new
    {
        ok = true,
        id = pack.Manifest.Id,
        name = pack.Manifest.Name,
        entities = pack.Entities.Definitions.Length,
        rules = pack.Rules.Rules.Length,
        schedules = pack.Rules.Schedules.Length,
        producers = incremental?.Catalog.Producers.Length ?? 0,
        upgrades = incremental?.Catalog.Upgrades.Length ?? 0,
        choices = incremental?.Choices.Choices.Length ?? 0,
        visualRecipes = presentation?.Visuals.Recipes.Length ?? 0,
        uiLayouts = presentation?.Ui.Layouts.Length ?? 0,
        timelines = presentation?.Catalog.Timelines.Length ?? 0,
        bindings = presentation?.Catalog.Bindings.Length ?? 0,
        gameplay2DActors = gameplay2D?.Actors.Count ?? 0,
        gameplay2DStatuses = gameplay2D?.Statuses.Count ?? 0,
        stateHash = StateHash(host),
    };
    if (json)
    {
        WriteJson(result);
    }
    else
    {
        Console.WriteLine($"Valid: {pack.Manifest.Id} ({pack.Manifest.Name})");
        Console.WriteLine($"Entities: {result.entities}; rules: {result.rules}; schedules: {result.schedules}");
        Console.WriteLine($"Producers: {result.producers}; upgrades: {result.upgrades}; build choices: {result.choices}");
        Console.WriteLine($"Visual recipes: {result.visualRecipes}; UI layouts: {result.uiLayouts}; presentation timelines: {result.timelines}; bindings: {result.bindings}");
        Console.WriteLine(gameplay2D is null
            ? "Gameplay2D content: none"
            : $"Gameplay2D actors: {gameplay2D.Actors.Count}; statuses: {gameplay2D.Statuses.Count}");
    }

    return 0;
}

static int RunProject(string directory, int ticks, int seed, bool json)
{
    if (ticks < 0)
    {
        throw new ArgumentException("--ticks must be non-negative.");
    }

    var pack = LoadPack(directory);
    using var host = HeadlessSystemicHost.Create(pack, seed);
    var stopwatch = Stopwatch.StartNew();
    host.RunTicks(ticks);
    stopwatch.Stop();
    var result = new
    {
        ok = true,
        id = pack.Manifest.Id,
        tick = host.Game.World.LogicalTick,
        seed,
        ticks,
        elapsedMs = stopwatch.Elapsed.TotalMilliseconds,
        entities = host.Game.World.Entities.Count(),
        rules = pack.Rules.Rules.Length,
        schedules = pack.Rules.Schedules.Length,
        stateHash = StateHash(host),
    };
    WriteResult(result, json);
    return 0;
}

static int SimulateProject(string directory, double seconds, int seed, string csvPath, bool json)
{
    if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds))
    {
        throw new ArgumentException("--seconds must be a positive finite number.");
    }

    var pack = LoadPack(directory);
    using var host = HeadlessSystemicHost.Create(pack, seed);
    var tickRate = pack.Manifest.TickRateHz;
    var requestedTicks = Math.Round(seconds * tickRate);
    if (requestedTicks > 10_000_000)
    {
        throw new ArgumentException("--seconds exceeds the 10,000,000-tick simulation limit.");
    }

    var totalTicks = (int)requestedTicks;
    var sampleEvery = Math.Max(1, tickRate);
    var fullPath = Path.GetFullPath(csvPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

    using (var writer = new StreamWriter(fullPath, append: false, new UTF8Encoding(false)))
    {
        writer.WriteLine("tick,seconds,entity,property,value");
        for (var tick = 0; tick <= totalTicks; tick++)
        {
            if (tick % sampleEvery == 0 || tick == totalTicks)
            {
                WriteSample(writer, host, tick, tickRate);
            }

            if (tick < totalTicks)
            {
                host.Step();
            }
        }
    }

    var result = new
    {
        ok = true,
        id = pack.Manifest.Id,
        seed,
        seconds,
        ticks = totalTicks,
        tickRate,
        csv = fullPath,
        bytes = new FileInfo(fullPath).Length,
        entities = host.Game.World.Entities.Count(),
        stateHash = StateHash(host),
    };
    WriteResult(result, json);
    return 0;
}

static void WriteSample(StreamWriter writer, HeadlessSystemicHost host, int tick, int tickRate)
{
    var seconds = (double)tick / tickRate;
    foreach (var entity in host.Game.World.Entities.OrderBy(entity => entity.Id))
    {
        if (entity is not DataEntity data)
        {
            continue;
        }

        foreach (var (property, value) in data.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.Write(tick.ToString(CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(seconds.ToString("0.###", CultureInfo.InvariantCulture));
            writer.Write(',');
            writer.Write(EscapeCsv(ContentId(host, entity.Id)));
            writer.Write(',');
            writer.Write(EscapeCsv(property));
            writer.Write(',');
            writer.Write(value.ToString("R", CultureInfo.InvariantCulture));
            writer.WriteLine();
        }
    }
}

static int DumpProject(string directory, bool json)
{
    var pack = LoadPack(directory);
    using var host = HeadlessSystemicHost.Create(pack, 1);
    var entities = host.Game.World.Entities.OrderBy(entity => entity.Id).Select(entity => new
    {
        id = entity.Id,
        contentId = ContentId(host, entity.Id),
        tags = entity.Tags.Order(StringComparer.Ordinal).ToArray(),
        properties = entity is DataEntity data
            ? data.Properties.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : null,
    }).ToArray();
    var result = new
    {
        ok = true,
        id = pack.Manifest.Id,
        name = pack.Manifest.Name,
        tickRateHz = pack.Manifest.TickRateHz,
        tick = host.Game.World.LogicalTick,
        contentFingerprint = pack.ContentFingerprint,
        rules = pack.Rules.Rules.Length,
        schedules = pack.Rules.Schedules.Length,
        entities,
        stateHash = StateHash(host),
    };
    WriteResult(result, json);
    return 0;
}

static int SaveProject(string directory, string output, int ticks, int seed, bool json)
{
    if (ticks < 0)
    {
        throw new ArgumentException("--ticks must be non-negative.");
    }

    var pack = LoadPack(directory);
    using var host = HeadlessSystemicHost.Create(pack, seed);
    host.RunTicks(ticks);
    var fullPath = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    var codec = new SystemicSaveCodec();
    codec.SaveToFile(host.Game, fullPath);
    var result = new
    {
        ok = true,
        id = pack.Manifest.Id,
        seed,
        tick = host.Game.World.LogicalTick,
        path = fullPath,
        bytes = new FileInfo(fullPath).Length,
        stateHash = StateHash(host),
    };
    WriteResult(result, json);
    return 0;
}

static int LoadProject(string directory, string savePath, bool json)
{
    var pack = LoadPack(directory);
    var jsonText = File.ReadAllText(Path.GetFullPath(savePath));
    using var host = HeadlessSystemicHost.Restore(pack, jsonText);
    var result = new
    {
        ok = true,
        id = pack.Manifest.Id,
        tick = host.Game.World.LogicalTick,
        seed = host.Game.World.Seed,
        entities = host.Game.World.Entities.Count(),
        path = Path.GetFullPath(savePath),
        stateHash = StateHash(host),
    };
    WriteResult(result, json);
    return 0;
}

static int VerifyProject(string directory, int ticks, int seed, bool json)
{
    if (ticks < 0)
    {
        throw new ArgumentException("--ticks must be non-negative.");
    }

    var pack = LoadPack(directory);
    using var first = HeadlessSystemicHost.Create(pack, seed);
    first.RunTicks(ticks);
    var firstHash = StateHash(first);
    var save = first.Save();
    using var restored = HeadlessSystemicHost.Restore(pack, save);
    var restoredHash = StateHash(restored);
    using var second = HeadlessSystemicHost.Create(pack, seed);
    second.RunTicks(ticks);
    var secondHash = StateHash(second);
    var ok = string.Equals(firstHash, secondHash, StringComparison.Ordinal)
        && string.Equals(firstHash, restoredHash, StringComparison.Ordinal);
    var result = new
    {
        ok,
        id = pack.Manifest.Id,
        seed,
        ticks,
        hash = firstHash,
        restoredHash,
        secondHash,
        saveBytes = Encoding.UTF8.GetByteCount(save),
    };
    WriteResult(result, json);
    if (!ok && !json)
    {
        Console.Error.WriteLine("Verification failed: run, restore, or second run produced a different state hash.");
    }

    return ok ? 0 : 4;
}

static int ReplayRecord(string directory, string output, int ticks, int seed, bool json)
{
    if (ticks <= 0)
    {
        throw new ArgumentException("--ticks must be positive.");
    }

    var pack = LoadPack(directory);
    using var host = HeadlessSystemicHost.Create(pack, seed);
    var target = new HostReplayTarget(host, static (_, _) =>
        throw new InvalidOperationException("This pack has no replay input mapping."));
    var recorder = new ReplayRecorder(target);
    for (var tick = 0; tick < ticks; tick++)
    {
        recorder.RecordTick(Array.Empty<ReplayInput>());
    }

    var artifact = recorder.Finish();
    var fullPath = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    File.WriteAllText(fullPath, ReplayCodec.Serialize(artifact), new UTF8Encoding(false));
    var result = new
    {
        ok = true,
        id = pack.Manifest.Id,
        seed,
        ticks = artifact.Ticks.Length,
        path = fullPath,
        bytes = new FileInfo(fullPath).Length,
        initialHash = artifact.InitialStateHash,
        finalHash = artifact.Ticks[^1].StateHash,
    };
    WriteResult(result, json);
    return 0;
}

static int ReplayVerify(string directory, string replayPath, bool json)
{
    var pack = LoadPack(directory);
    var artifact = ReplayCodec.Deserialize(File.ReadAllText(Path.GetFullPath(replayPath)));
    using var host = HeadlessSystemicHost.Create(pack, artifact.Seed);
    var target = new HostReplayTarget(host, static (_, _) =>
        throw new InvalidOperationException("This pack has no replay input mapping."));
    var verification = ReplayRunner.Verify(artifact, target);
    var result = new
    {
        ok = verification.Success,
        id = pack.Manifest.Id,
        seed = artifact.Seed,
        ticks = artifact.Ticks.Length,
        mismatch = verification.Mismatch is null ? null : new
        {
            verification.Mismatch.Code,
            verification.Mismatch.Tick,
            verification.Mismatch.Expected,
            verification.Mismatch.Actual,
            verification.Mismatch.InputIndex,
        },
    };
    WriteResult(result, json);
    if (!verification.Success && !json)
    {
        Console.Error.WriteLine($"Replay mismatch: {verification.Mismatch!.Code} at tick {verification.Mismatch.Tick}.");
    }

    return verification.Success ? 0 : 4;
}

static int AssetsValidate(string directory, bool json)
{
    var recipes = LoadRecipes(directory);
    var errors = new List<object>();
    var ids = new HashSet<string>(StringComparer.Ordinal);
    foreach (var recipe in recipes)
    {
        if (!ids.Add(recipe.Id))
        {
            errors.Add(new { recipe = recipe.Id, code = "asset.duplicate", message = "Duplicate recipe id." });
            continue;
        }

        if (recipe.CanvasWidth is < 1 or > 256 || recipe.CanvasHeight is < 1 or > 256)
        {
            errors.Add(new { recipe = recipe.Id, code = "asset.canvas", message = "Canvas must be between 1 and 256 pixels." });
            continue;
        }

        if (recipe.Palette.IsDefaultOrEmpty)
        {
            errors.Add(new { recipe = recipe.Id, code = "asset.palette", message = "Recipe has no palette." });
            continue;
        }

        try
        {
            var renderedStates = 0;
            foreach (var state in VisualStates(recipe))
            {
                var pixels = ProceduralRasterizer.Rasterize(recipe, 0, state);
                var opaque = false;
                for (var index = 3; index < pixels.Pixels.Length; index += 4)
                {
                    if (pixels.Pixels.Span[index] != 0)
                    {
                        opaque = true;
                        break;
                    }
                }

                if (opaque)
                {
                    renderedStates++;
                }
            }

            if (renderedStates == 0)
            {
                errors.Add(new { recipe = recipe.Id, code = "asset.empty", message = "Recipe rasterized to an empty canvas in every state." });
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            errors.Add(new { recipe = recipe.Id, code = "asset.raster", message = exception.Message });
        }
    }

    var result = new
    {
        ok = errors.Count == 0,
        recipes = recipes.Length,
        errors,
    };
    WriteResult(result, json);
    return errors.Count == 0 ? 0 : 1;
}

/// <summary>Every state a recipe can render, with the default state first.</summary>
static string?[] VisualStates(VisualRecipe recipe) =>
[
    null,
    .. recipe.Layers
        .Concat(recipe.Parts.SelectMany(part => part.Recipe.Layers))
        .Select(layer => layer.State)
        .Where(state => !string.IsNullOrWhiteSpace(state))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal),
];

static int AssetsSheet(string directory, string output, int scale, bool json)
{
    if (scale is < 1 or > 16)
    {
        throw new ArgumentException("--scale must be between 1 and 16.");
    }

    var recipes = LoadRecipes(directory);
    if (recipes.Length == 0)
    {
        throw new ArgumentException("The pack has no visual recipes.");
    }

    var cells = recipes.SelectMany(recipe => VisualStates(recipe)
        .Select(state => (Recipe: recipe, State: state))).ToArray();
    var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cells.Length)));
    var rows = (int)Math.Ceiling(cells.Length / (double)columns);
    var cellWidth = (recipes.Max(recipe => recipe.CanvasWidth) * scale) + 16;
    var cellHeight = (recipes.Max(recipe => recipe.CanvasHeight) * scale) + 28;
    using var sheet = new SKBitmap(new SKImageInfo(columns * cellWidth, rows * cellHeight,
        SKColorType.Rgba8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(sheet);
    canvas.Clear(new SKColor(18, 16, 24));
    using var label = new SKPaint { Color = new SKColor(232, 220, 200), IsAntialias = true };
    using var font = new SKFont(SKTypeface.Default, 12);
    using var draw = new SKPaint { IsAntialias = false };
    for (var index = 0; index < cells.Length; index++)
    {
        var (recipe, state) = cells[index];
        var column = index % columns;
        var row = index / columns;
        var x = (column * cellWidth) + 8;
        var y = (row * cellHeight) + 20;
        using var bitmap = ToBitmap(ProceduralRasterizer.Rasterize(recipe, 0, state));
        canvas.DrawBitmap(bitmap, SKRect.Create(x, y, recipe.CanvasWidth * scale, recipe.CanvasHeight * scale), new SKSamplingOptions(SKFilterMode.Nearest));
        canvas.DrawText(state is null ? recipe.Id : $"{recipe.Id}:{state}", x, y - 6, SKTextAlign.Left, font, label);
    }

    var fullPath = Path.GetFullPath(output);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    using (var image = SKImage.FromBitmap(sheet))
    using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
    using (var stream = File.Create(fullPath))
    {
        data.SaveTo(stream);
    }

    var result = new
    {
        ok = true,
        recipes = recipes.Length,
        path = fullPath,
        width = sheet.Width,
        height = sheet.Height,
        bytes = new FileInfo(fullPath).Length,
    };
    WriteResult(result, json);
    return 0;
}

static int AssetsAtlas(string directory, string output, string manifest, bool json)
{
    var recipes = LoadRecipes(directory);
    if (recipes.Length == 0)
    {
        throw new ArgumentException("The pack has no visual recipes.");
    }

    var cells = recipes.SelectMany(recipe => VisualStates(recipe)
        .Select(state => (Recipe: recipe, State: state))).ToArray();
    var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(cells.Length)));
    var rows = (int)Math.Ceiling(cells.Length / (double)columns);
    var cellWidth = recipes.Max(recipe => recipe.CanvasWidth) + 2;
    var cellHeight = recipes.Max(recipe => recipe.CanvasHeight) + 2;
    using var atlas = new SKBitmap(new SKImageInfo(columns * cellWidth, rows * cellHeight,
        SKColorType.Rgba8888, SKAlphaType.Premul));
    using var canvas = new SKCanvas(atlas);
    canvas.Clear(SKColors.Transparent);
    using var draw = new SKPaint { IsAntialias = false };
    var frames = new List<object>();
    for (var index = 0; index < cells.Length; index++)
    {
        var (recipe, state) = cells[index];
        var x = ((index % columns) * cellWidth) + 1;
        var y = ((index / columns) * cellHeight) + 1;
        using var bitmap = ToBitmap(ProceduralRasterizer.Rasterize(recipe, 0, state));
        canvas.DrawBitmap(bitmap, SKRect.Create(x, y, recipe.CanvasWidth, recipe.CanvasHeight), new SKSamplingOptions(SKFilterMode.Nearest));
        frames.Add(new
        {
            id = recipe.Id,
            state,
            x,
            y,
            width = recipe.CanvasWidth,
            height = recipe.CanvasHeight,
            pivotX = recipe.CanvasWidth / 2,
            pivotY = recipe.CanvasHeight,
        });
    }

    var atlasPath = Path.GetFullPath(output);
    var manifestPath = Path.GetFullPath(manifest);
    Directory.CreateDirectory(Path.GetDirectoryName(atlasPath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
    using (var image = SKImage.FromBitmap(atlas))
    using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
    using (var stream = File.Create(atlasPath))
    {
        data.SaveTo(stream);
    }

    File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
    {
        atlas = atlasPath,
        width = atlas.Width,
        height = atlas.Height,
        frames,
    }, CliJson.Options), new UTF8Encoding(false));
    var result = new
    {
        ok = true,
        recipes = recipes.Length,
        atlas = atlasPath,
        manifest = manifestPath,
        width = atlas.Width,
        height = atlas.Height,
    };
    WriteResult(result, json);
    return 0;
}

static VisualRecipe[] LoadRecipes(string directory)
{
    var pack = LoadPack(directory);
    var presentation = pack.Modules.Select(module => module.Content).OfType<PresentationModuleContent>().SingleOrDefault();
    return presentation?.Visuals.Recipes.ToArray() ?? [];
}

static SKBitmap ToBitmap(PixelBuffer pixels)
{
    var bitmap = new SKBitmap(new SKImageInfo(pixels.Width, pixels.Height,
        SKColorType.Rgba8888, SKAlphaType.Unpremul));
    pixels.Pixels.Span.CopyTo(bitmap.GetPixelSpan());
    return bitmap;
}

static string StateHash(HeadlessSystemicHost host)
{
    var target = new HostReplayTarget(host, static (_, _) =>
        throw new InvalidOperationException("This pack has no replay input mapping."));
    return ReplayStateHash.Compute(target.CaptureState().Span);
}

static string ContentId(HeadlessSystemicHost host, int id) =>
    host.Game.EntityIds.FirstOrDefault(pair => pair.Value == id).Key ?? $"entity:{id}";

static string EscapeCsv(string value) =>
    value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
        ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\""
        : value;

static int Trace(string directory, string eventName, string? source, string? target, bool json)
{
    var content = LoadPack(directory);
    var materialized = content.CreateWorld(seed: 1, valueScale: 1);
    using var game = new SystemicGameBuilder(content, materialized).Build();
    var sourceId = source is null ? 0 : game.EntityId(source);
    var targetId = target is null ? 0 : game.EntityId(target);
    var trace = game.Trace(new SimulationFact(eventName, sourceId, targetId));
    string ContentName(int id) => game.EntityIds.FirstOrDefault(pair => pair.Value == id).Key ?? $"entity:{id}";

    if (json)
    {
        WriteJson(new
        {
            ok = true,
            @event = eventName,
            facts = trace.Facts.Select(entry => new
            {
                entry.Sequence,
                depth = entry.Fact.Depth,
                type = entry.Fact.Type,
                source = ContentName(entry.Fact.SourceId),
                target = ContentName(entry.Fact.TargetId),
                rules = entry.MatchedRules,
            }),
            result = new
            {
                trace.Result.FactsProcessed,
                trace.Result.RulesMatched,
                trace.Result.EffectsApplied,
                trace.Result.CutoffReason,
                trace.Result.RolledBack,
            },
        });
        return 0;
    }

    foreach (var entry in trace.Facts)
    {
        var rules = entry.MatchedRules.IsEmpty ? "-" : string.Join(", ", entry.MatchedRules);
        Console.WriteLine($"[{entry.Sequence}] depth={entry.Fact.Depth} {entry.Fact.Type} ({ContentName(entry.Fact.SourceId)} -> {ContentName(entry.Fact.TargetId)}) rules: {rules}");
    }

    var result = trace.Result;
    Console.WriteLine($"Facts: {result.FactsProcessed}; rules: {result.RulesMatched}; effects: {result.EffectsApplied}; cutoff: {result.CutoffReason ?? "none"}; rolled back: {result.RolledBack}");
    return 0;
}

static async Task<int> WatchAsync(string root)
{
    var fullPath = Path.GetFullPath(root);
    _ = LoadPack(fullPath);
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
                var pack = LoadPack(fullPath);
                Console.WriteLine($"Valid: {pack.Manifest.Id} ({pack.Manifest.Name})");
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

static ContentPack LoadPack(string directory)
{
    var fullPath = Path.GetFullPath(directory);
    var registry = new ContentModuleRegistry()
        .Add(new Gameplay2DContentModule())
        .Add(new IncrementalContentModule())
        .Add(new PresentationContentModule());
    return ContentPackLoader.Load(fullPath, registry);
}

static void CreateProject(string destination, string templateName, bool json)
{
    if (!string.Equals(templateName, "systemic", StringComparison.Ordinal))
    {
        throw new ArgumentException($"Unknown template '{templateName}'. The only template is 'systemic'.");
    }

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
    Directory.CreateDirectory(fullPath);
    Write("game.json", $$"""
        {
          "schemaVersion": 3,
          "id": "sample.{{slug}}",
          "name": {{JsonSerializer.Serialize(directoryName)}},
          "tickRateHz": 60,
          "maxCatchUpSteps": 4,
          "rules": "rules.json",
          "entities": "entities.json",
          "modules": {
            "systemic.presentation": {
              "visuals": "visuals.json",
              "ui": "ui.json",
              "presentation": "presentation.json"
            }
          }
        }
        """);
    Write("entities.json", """
        {
          "schemaVersion": 2,
          "entities": [
            {
              "id": "resource.energy",
              "kind": "resource",
              "tags": ["resource"],
              "properties": { "balance": 0 }
            },
            {
              "id": "actor.generator",
              "kind": "actor",
              "tags": ["generator"],
              "properties": { "rate": 1 },
              "visualRecipeId": "template.generator"
            },
            {
              "id": "choice.boost",
              "kind": "prop",
              "tags": ["ui.choice"],
              "properties": { "selected": 0, "cost": 1 },
              "visualRecipeId": "template.icon"
            }
          ]
        }
        """);
    Write("rules.json", """
        {
          "schemaVersion": 2,
          "schedules": [
            {
              "id": "template.pulse",
              "event": "template.tick",
              "everyTicks": 60,
              "sourceEntityId": "actor.generator",
              "targetEntityId": "resource.energy"
            }
          ],
          "rules": [
            {
              "id": "template.produce",
              "on": "template.tick",
              "priority": 0,
              "when": [
                { "kind": "hasTag", "subject": "source", "tag": "generator" }
              ],
              "effects": [
                { "kind": "adjustProperty", "subject": "target", "property": "balance", "amount": 1 }
              ]
            }
          ]
        }
        """);
    Write("visuals.json", """
        {
          "schemaVersion": 2,
          "palettes": {
            "template.core": {
              "dark": "#1b1420",
              "body": "#e0a14c",
              "light": "#f6d79a"
            }
          },
          "recipes": [
            {
              "id": "template.generator",
              "kind": "actor",
              "seed": 7,
              "canvasWidth": 24,
              "canvasHeight": 24,
              "paletteId": "template.core",
              "layers": [
                { "shape": "rect", "colorRole": "dark", "x": 4, "y": 6, "width": 16, "height": 14 },
                { "shape": "rect", "colorRole": "body", "x": 6, "y": 8, "width": 12, "height": 10 },
                { "shape": "ellipse", "colorRole": "light", "x": 9, "y": 4, "width": 6, "height": 6 }
              ]
            },
            {
              "id": "template.spark",
              "kind": "particle",
              "seed": 11,
              "canvasWidth": 12,
              "canvasHeight": 12,
              "paletteId": "template.core",
              "layers": [
                { "shape": "ellipse", "colorRole": "light", "x": 3, "y": 3, "width": 6, "height": 6 }
              ]
            },
            {
              "id": "template.icon",
              "kind": "prop",
              "seed": 13,
              "canvasWidth": 16,
              "canvasHeight": 16,
              "paletteId": "template.core",
              "layers": [
                { "shape": "rect", "colorRole": "dark", "x": 3, "y": 3, "width": 10, "height": 10 },
                { "shape": "rect", "colorRole": "body", "x": 5, "y": 5, "width": 6, "height": 6 },
                { "shape": "ellipse", "colorRole": "light", "x": 7, "y": 7, "width": 2, "height": 2 }
              ]
            }
          ]
        }
        """);
    Write("ui.json", """
        {
          "schemaVersion": 2,
          "layouts": [
            {
              "id": "template.hud",
              "root": {
                "type": "column",
                "children": [
                  { "type": "text", "text": "Systemic template" },
                  { "type": "value", "text": "Energy", "bind": "world.resource.energy.balance" },
                  { "type": "bar", "text": "Next pulse", "bind": "world.resource.energy.balance", "minimum": 0, "maximum": 60 },
                  { "type": "icon", "visualRecipeId": "template.generator" }
                ]
              }
            }
          ]
        }
        """);
    Write("presentation.json", """
        {
          "schemaVersion": 2,
          "timelines": [
            {
              "id": "template.pulse",
              "visualRecipeId": "template.spark",
              "durationTicks": 12,
              "loop": false,
              "keyframes": [
                { "tick": 0, "scale": 0.5, "opacity": 0 },
                { "tick": 4, "scale": 1, "opacity": 1 },
                { "tick": 11, "scale": 0.5, "opacity": 0 }
              ]
            }
          ],
          "bindings": [
            { "event": "template.tick", "timelineId": "template.pulse", "anchor": "target" }
          ]
        }
        """);

    if (json)
    {
        WriteJson(new { ok = true, path = fullPath, template = templateName, files = new[] { "game.json", "entities.json", "rules.json", "visuals.json", "ui.json", "presentation.json" } });
    }
    else
    {
        Console.WriteLine($"Created systemic game project: {fullPath}");
        Console.WriteLine($"Next: systemic run {fullPath} --ticks 600");
    }

    void Write(string file, string contents) => File.WriteAllText(Path.Combine(fullPath, file), contents, new UTF8Encoding(false));
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

static void WriteResult(object result, bool json)
{
    if (json)
    {
        WriteJson(result);
        return;
    }

    Console.WriteLine(JsonSerializer.Serialize(result, CliJson.Options));
}

static void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, CliJson.Options));

static void WriteError(string code, string message, object? details = null)
{
    WriteJson(new { ok = false, code, message, details });
}

static int Fail(string code, string message, int exitCode, object? details = null)
{
    if (Environment.GetCommandLineArgs().Contains("--json", StringComparer.Ordinal))
    {
        WriteError(code, message, details);
    }
    else
    {
        var prefix = code switch
        {
            "content.invalid" => "Content error",
            "content.gameplay2d" => "Gameplay2D content error",
            "content.reference" => "Content reference error",
            "save.invalid" => "Save error",
            "io.error" => "File error",
            "usage.invalid" => "Argument error",
            _ => "Runtime error",
        };
        Console.Error.WriteLine($"{prefix}: {message}");
    }

    return exitCode;
}

static void PrintUsage() =>
    Console.WriteLine("Usage: systemic <new|validate|run|simulate|dump|save|load|verify|replay|assets|watch|trace> ...");

static class CliJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
