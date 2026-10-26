namespace Systemic.Kernel;

/// <summary>Compile-time event token used to register rules without a runtime enum switch.</summary>
public readonly record struct EventType<TEvent> where TEvent : KernelEvent;

/// <summary>Shared causal identity carried by every committed simulation event.</summary>
public readonly record struct EventCausality(
    long RootActionId = 0,
    long ParentEventId = 0,
    int ChainId = 0,
    int Depth = 0);

/// <summary>Base for typed commands and committed facts accepted by the kernel.</summary>
public abstract record KernelEvent(int SourceId, int TargetId = 0)
{
    private EventCausality _causality;
    private long _eventId;

    public EventCausality Causality
    {
        get => _causality;
        init => _causality = value;
    }

    public long EventId
    {
        get => _eventId;
        init => _eventId = value;
    }

    internal void SetDispatchMetadata(long eventId, EventCausality causality)
    {
        _eventId = eventId;
        _causality = causality;
    }

    public int Count { get; init; } = 1;
    public bool Ordinary { get; init; }
    public long CauseId => Causality.ParentEventId != 0 ? Causality.ParentEventId : Causality.RootActionId;
    public int ChainId => Causality.ChainId;
    public int Depth => Causality.Depth;
    public long Serial => EventId;
}

public enum ContactPhase
{
    Began,
    Persisted,
    Ended,
}

/// <summary>Engine-neutral contact observation that a host or portable overlap system can publish to rules.</summary>
public sealed record ContactFact(int SourceId, int TargetId, ContactPhase Phase)
    : KernelEvent(SourceId, TargetId);

/// <summary>A committed action published to rules that observe simulation facts.</summary>
public sealed record CommittedEvent(int SourceId, int TargetId, CommittedAction Action)
    : KernelEvent(SourceId, TargetId);
