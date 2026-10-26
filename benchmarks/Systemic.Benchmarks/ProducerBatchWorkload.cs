using Systemic.Kernel;

namespace Systemic.Benchmarks;

internal sealed class ProducerBatchWorkload
{
    private const int ProducerCount = 50_000;
    private const string ProducedProperty = "produced";
    private readonly World _world;
    private readonly EventEngine _engine;
    private readonly EventEngine _queryOnlyEngine;
    private readonly EventEngine _emptyEngine;
    private readonly ProducerTick _tick;
    private readonly Action<DataEntity> _produce;
    private long _visits;

    public ProducerBatchWorkload()
    {
        _world = new World(seed: 1, valueScale: 1);
        var output = _world.Add(new DataEntity(["resource"], ["can_be_produced"], ("amount", 0)));
        for (var index = 0; index < ProducerCount; index++)
        {
            var producer = _world.Add(new DataEntity(["producer"], ["can_produce"], (ProducedProperty, 0)));
            producer.SetLink("output", output.Id);
        }

        var query = new EntityQuery<DataEntity>(
            tags: ["producer"],
            capabilities: ["can_produce"],
            links: [new LinkRequirement("output", "resource", "can_be_produced")]);
        _produce = ProduceOne;
        _engine = new EngineBuilder()
            .OnBatch<ProducerTick, DataEntity>(
                "benchmark.producer_batch",
                query,
                context => context.ForEachTarget(_produce),
                transactional: false)
            .Build();
        _queryOnlyEngine = new EngineBuilder()
            .OnBatch<ProducerTick, DataEntity>(
                "benchmark.producer_batch_noop",
                query,
                static context => context.ForEachTarget(static _ => { }),
                transactional: false)
            .Build();
        _emptyEngine = new EngineBuilder().Build();
        _tick = new ProducerTick(output.Id);

        void ProduceOne(DataEntity producer)
        {
            if (!producer.TryGetProperty(_world, ProducedProperty, out var produced)
                || !producer.TrySetProperty(_world, ProducedProperty, produced + 1))
            {
                throw new InvalidOperationException("A producer could not advance its output count.");
            }

            _visits++;
        }
    }

    public void Tick()
    {
        var priorVisits = _visits;
        if (!_engine.ExecuteReusable(_world, _tick) || _visits - priorVisits != ProducerCount)
        {
            throw new InvalidOperationException("The generic batch path did not visit all 50,000 producers.");
        }
    }

    public void TickQueryOnly() => _queryOnlyEngine.ExecuteReusable(_world, _tick);

    public void TickEngineBase() => _emptyEngine.ExecuteReusable(_world, _tick);

    private sealed record ProducerTick(int SourceId) : KernelEvent(SourceId);
}
