using System.Collections.Immutable;

namespace Systemic.Kernel;

public enum ActionDisposition
{
    Applied,
    NoChange,
    Rejected,
}

public class ActionContext : IDisposable
{
    private readonly EventEngine _engine;
    private readonly List<ResourcePayment> _payments = [];
    private readonly List<CommittedEffect> _effects = [];
    private WorldScope _scope;
    private string _rule = "";
    private int _budget;
    private int _applied;
    private int? _repetitions;
    private double? _production;
    private bool _rejected;
    private bool _closed;
    private bool _disposed;

    internal ActionContext(EventEngine engine) => _engine = engine;

    public World World { get; private set; } = null!;
    public KernelEvent Event { get; private set; } = null!;
    public long Id { get; private set; }
    public long OriginActionId { get; set; }
    public object? Detail { get; set; }
    public ActionOutcome? Outcome { get; set; }
    public ActionDisposition Disposition { get; private set; } = ActionDisposition.NoChange;
    public int Repetitions { get => _repetitions ?? Outcome?.Count ?? 1; set => _repetitions = value; }
    public bool Optional { get; set; }
    public bool CanRun => !_closed && !_rejected;

    internal void Reset(World world, KernelEvent domainEvent, string rule, long serial, int budget, bool optional,
        bool resolved = false)
    {
        World = world;
        Event = domainEvent;
        Id = serial;
        _rule = rule;
        _budget = budget;
        Optional = optional;
        OriginActionId = 0;
        Detail = null;
        _production = null;
        Outcome = null;
        Disposition = ActionDisposition.NoChange;
        _repetitions = null;
        _applied = 0;
        _closed = _disposed = false;
        _rejected = !resolved && (world.ById(domainEvent.SourceId) is not { ToRemove: false }
            || world.ById(domainEvent.TargetId) is not { ToRemove: false });

        _scope = world.BeginScope();
    }

    public void Reject() => _rejected = true;

    public bool Pay(ResourceEntity resource, double amount)
    {
        EnsureOpen();
        if (_rejected || !Owns(resource) || !resource.TrySpend(World, amount))
        {
            _rejected = true;

            return false;
        }

        _payments.Add(new ResourcePayment(resource.Id, amount));

        return true;
    }

    public T Create<T>(T entity) where T : Entity
    {
        if (TryEffect())
        {
            World.Add(entity);
            AddEffect(new("created", entity.Id, 1));
        }

        return entity;
    }

    public T Edit<T>(T entity) where T : Entity
    {
        if (!Owns(entity))
        {
            throw new ArgumentException("The entity must be live in the action world.", nameof(entity));
        }

        if (TryEffect())
        {
            AddEffect(new("state", entity.Id, 0));
        }

        return World.Edit(entity);
    }

    public bool Produce(ResourceEntity resource, double amount)
    {
        var production = (_production ?? 0) + amount;
        if (!TryEffect() || !Owns(resource) || !World.HasCapability(resource, EntityCapabilities.BeProduced)
            || Repetitions <= 0 || (Outcome is null && !double.IsFinite(production))
            || !resource.CanProduce(World, amount, out _))
        {
            _rejected = true;

            return false;
        }

        var accepted = resource.TryProduce(World, amount, Repetitions);
        if (Outcome is null)
        {
            _production = production;
        }

        AddEffect(new("production", resource.Id, accepted));

        return true;
    }

    public bool Consume(Entity entity)
    {
        if (!TryEffect() || !Owns(entity) || World.IsRequired(entity))
        {
            _rejected = true;

            return false;
        }

        entity.ToRemove = true;
        AddEffect(new("consumed", entity.Id, 1));

        return true;
    }

    public bool Complete()
    {
        EnsureOpen();
        var outcome = Outcome ?? (_production is { } production
            ? ActionOutcome.Of(_rule, ("production", production)) : new ActionOutcome(_rule, []));
        _rejected |= (IsReaction && _applied > _budget)
            || !outcome.Values.AllFinite() || !double.IsFinite(outcome.PopulationScale)
            || outcome.PopulationScale <= 0 || outcome.Count <= 0 || Repetitions <= 0;
        if (_rejected || (_payments.Count == 0 && _effects.Count == 0))
        {
            _scope.Rollback();
            _closed = true;
            if (_rejected)
            {
                Disposition = ActionDisposition.Rejected;
                _engine.RejectAction(World, Event, _rule, Optional);
            }
            else
            {
                Disposition = ActionDisposition.NoChange;
            }

            return false;
        }

        var fact = new CommittedAction(Id, Event.EventId, _rule, Event.SourceId, Event.TargetId, outcome,
            [.. _payments])
        {
            Effects = [.. _effects],
            OriginActionId = OriginActionId,
            Detail = Detail,
            Repetitions = Repetitions,
        };
        _scope.Commit();
        _closed = true;
        Disposition = ActionDisposition.Applied;
        _engine.PublishAction(World, Event, fact, IsReaction ? _applied : 0);

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (!_closed)
        {
            _scope.Rollback();
        }
        _closed = _disposed = true;
        _payments.Clear();
        _effects.Clear();
        _engine.ReleaseAction(this);
        GC.SuppressFinalize(this);
    }

    public bool RecordEffect(CommittedEffect effect)
    {
        if (!TryEffect() || !double.IsFinite(effect.Amount))
        {
            _rejected = true;

            return false;
        }

        AddEffect(effect);

        return true;
    }

    private void AddEffect(CommittedEffect effect)
    {
        _applied++;
        for (var index = 0; index < _effects.Count; index++)
        {
            var previous = _effects[index];
            if (previous.Kind == effect.Kind && previous.TargetId == effect.TargetId)
            {
                _effects[index] = previous with { Amount = previous.Amount + effect.Amount };

                return;
            }
        }

        _effects.Add(effect);
    }

    private bool Owns(Entity entity) => entity.OwnerWorld == World && !entity.ToRemove;

    private bool IsReaction => Optional || Event.Causality.Depth > 0 && !Event.Ordinary;

    private bool TryEffect()
    {
        EnsureOpen();
        if (_rejected || (IsReaction && _applied >= _budget))
        {
            _rejected = true;

            return false;
        }

        return true;
    }

    private void EnsureOpen()
    {
        if (_closed)
        {
            throw new InvalidOperationException("The action is closed.");
        }
    }
}

public sealed class ActionContext<TEvent> : ActionContext
    where TEvent : KernelEvent
{
    public new TEvent Event => (TEvent)base.Event;

    internal ActionContext(EventEngine engine) : base(engine) { }

    internal void Reset(World world, TEvent domainEvent, string rule, long serial, int budget, bool optional,
        bool resolved = false) =>
        base.Reset(world, domainEvent, rule, serial, budget, optional, resolved);
}
