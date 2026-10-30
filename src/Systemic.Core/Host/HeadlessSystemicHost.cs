using Systemic.Content;
using Systemic.Rules;
using Systemic.Save;

namespace Systemic.Host;

/// <summary>Core host contract shared by headless checks and engine adapters.</summary>
public interface ISystemicHostAdapter
{
    SystemicGame Game { get; }
    FixedStepResult Advance(TimeSpan elapsed);
    void Step();
    RuleDispatchResult Publish(SimulationFact fact);
    RuleDispatchTrace Trace(SimulationFact fact);
    string Save();
}

/// <summary>Runs the engine-neutral Core host without rendering or engine services.</summary>
public sealed class HeadlessSystemicHost : ISystemicHostAdapter, IDisposable
{
    private readonly SystemicSaveCodec _saveCodec;

    public HeadlessSystemicHost(SystemicGame game, SystemicSaveCodec? saveCodec = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        Game = game;
        _saveCodec = saveCodec ?? new SystemicSaveCodec();
    }

    public SystemicGame Game { get; }

    /// <summary>Disposes the hosted game, which stops its worker threads and releases its state.</summary>
    public void Dispose() => Game.Dispose();

    public static HeadlessSystemicHost Create(
        ContentPack content,
        int seed,
        double valueScale = 1,
        Action<SystemicGameBuilder>? configure = null,
        SystemicSaveCodec? saveCodec = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var materialized = content.CreateWorld(seed, valueScale);
        var builder = new SystemicGameBuilder(content, materialized);
        configure?.Invoke(builder);
        return new HeadlessSystemicHost(builder.Build(), saveCodec);
    }

    public static HeadlessSystemicHost FromDirectory(
        string rootDirectory,
        int seed,
        double valueScale = 1,
        Action<SystemicGameBuilder>? configure = null,
        SystemicSaveCodec? saveCodec = null)
    {
        var content = ContentPackLoader.Load(rootDirectory);
        return Create(content, seed, valueScale, configure, saveCodec);
    }

    public static HeadlessSystemicHost Restore(
        ContentPack content,
        string save,
        Action<SystemicGameBuilder>? configure = null,
        SystemicSaveCodec? saveCodec = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        var codec = saveCodec ?? new SystemicSaveCodec();
        var materialized = codec.Load(content, save);
        var builder = new SystemicGameBuilder(content, materialized);
        configure?.Invoke(builder);
        return new HeadlessSystemicHost(builder.Build(), codec);
    }

    public FixedStepResult Advance(TimeSpan elapsed) => Game.Advance(elapsed);

    public void Step() => Game.Step();

    public void RunTicks(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "A headless run is limited to 1,000,000 ticks per call.");
        }

        for (var tick = 0; tick < count; tick++)
        {
            Game.Step();
        }
    }

    public RuleDispatchResult Publish(SimulationFact fact) => Game.Publish(fact);

    public RuleDispatchTrace Trace(SimulationFact fact) => Game.Trace(fact);

    public string Save() => _saveCodec.Save(Game);
}
