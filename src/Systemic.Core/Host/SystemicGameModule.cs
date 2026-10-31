using System.Text.Json;
using Systemic.Extensions;
using Systemic.Kernel;

namespace Systemic.Host;

/// <summary>A game-owned composition root that registers systems, commands, and data services.</summary>
/// <remarks>
/// The framework does not know what a game module is beyond this contract. A module is also an
/// extension module so it can own systems and saved state through the same registration path.
/// Save documents record <see cref="ISystemicExtensionModule.Version"/> and refuse to load when a
/// module's version changed.
/// </remarks>
public interface ISystemicGameModule : ISystemicExtensionModule
{
    /// <summary>Registers the module's systems, commands, services, and content graph.</summary>
    /// <param name="builder">The host builder for this composition.</param>
    void Compose(SystemicGameBuilder builder);
}

/// <summary>Marks a typed input that a game module handles outside the tick schedule.</summary>
public interface ISystemicCommand
{
}

/// <summary>Handles one typed game command.</summary>
/// <typeparam name="TCommand">The command type registered on the builder.</typeparam>
public interface ISystemicCommandHandler<in TCommand>
    where TCommand : ISystemicCommand
{
    /// <summary>Runs the command inside its own deterministic World transaction.</summary>
    /// <param name="command">The command payload.</param>
    /// <param name="context">The command-scoped World and service access.</param>
    void Handle(TCommand command, SystemicCommandContext context);
}

/// <summary>Marks a typed notification emitted by a host or game module.</summary>
public interface ISystemicEvent
{
}

/// <summary>Handles one typed event.</summary>
/// <typeparam name="TEvent">The event type registered on the builder.</typeparam>
public interface ISystemicEventHandler<in TEvent>
    where TEvent : ISystemicEvent
{
    /// <summary>Runs the handler in registration order.</summary>
    /// <param name="event">The event payload.</param>
    /// <param name="context">The event-scoped World and service access.</param>
    /// <remarks>
    /// Order is registration order within one event registration. A handler registered for a base
    /// event type or <see cref="ISystemicEvent"/> receives every assignable event, and dispatch
    /// walks registrations in the order they were added, so a base registration interleaves with
    /// derived ones rather than running after them.
    /// </remarks>
    void Handle(TEvent @event, SystemicEventContext context);
}

/// <summary>A UI declaration resolved to the values a command needs.</summary>
/// <param name="LayoutId">The layout that declared the clicked action.</param>
/// <param name="ActionId">The declared action ID.</param>
/// <param name="OwnerEntityId">The entity that owns the click.</param>
/// <param name="Amount">The clicked node's amount, or zero when it has none.</param>
public readonly record struct SystemicUiAction(
    string LayoutId,
    string ActionId,
    int OwnerEntityId,
    int Amount);

/// <summary>Command-scoped access to the World and host services.</summary>
/// <remarks>Write through <see cref="Commands"/>; the host applies and commits them only when the handler returns.</remarks>
public sealed class SystemicCommandContext
{
    private readonly SystemicSystemContext _context;
    private readonly List<ISystemicEvent> _events = [];
    private bool _finished;

    private string? _rejection;

    internal SystemicCommandContext(SystemicSystemContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Gets whether the handler rejected the command.</summary>
    public bool IsRejected => _rejection is not null;

    /// <summary>Gets the rejection reason, or null when the command was accepted.</summary>
    public string? RejectionReason => _rejection;

    /// <summary>Rejects the command and asks the host to roll back its transaction.</summary>
    /// <param name="reason">Stable, human-readable reason such as <c>insufficient.funds</c>.</param>
    /// <remarks>
    /// Rejection is normal play, not a failure. Return immediately after calling this method; the
    /// host rolls back the command's World and service mutations and reports the reason to the
    /// caller instead of committing.
    /// </remarks>
    public void Reject(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ThrowIfFinished();
        _rejection = reason;
    }

    /// <summary>Queues a typed event that dispatches after the command's World writes apply.</summary>
    /// <param name="event">The event payload.</param>
    /// <remarks>
    /// Events dispatch inside the command's transaction, so a throwing handler rolls the command
    /// back. A rejected command discards its queued events with its writes.
    /// </remarks>
    public void EmitEvent<TEvent>(TEvent @event)
        where TEvent : ISystemicEvent
    {
        ArgumentNullException.ThrowIfNull(@event);
        ThrowIfFinished();
        _events.Add(@event);
    }

    internal IReadOnlyList<ISystemicEvent> PendingEvents => _events;

    /// <summary>Gets the logical tick the command executes against.</summary>
    public int Tick
    {
        get
        {
            ThrowIfFinished();
            return _context.Tick;
        }
    }

    /// <summary>Gets the read-only World view for the command.</summary>
    public ReadOnlyWorldView World
    {
        get
        {
            ThrowIfFinished();
            return _context.World;
        }
    }

    /// <summary>Gets the command-scoped deferred World writer.</summary>
    public WorldCommandBuffer Commands
    {
        get
        {
            ThrowIfFinished();
            return _context.Commands;
        }
    }

    /// <summary>Gets a registered host service by its registration type.</summary>
    /// <typeparam name="TService">Service type registered with the game builder.</typeparam>
    public TService GetService<TService>() where TService : notnull
    {
        ThrowIfFinished();
        return _context.GetService<TService>();
    }

    internal void ApplyDeferredCommands()
    {
        ThrowIfFinished();
        _context.ApplyDeferredCommands();
    }

    internal void Finish()
    {
        _finished = true;
        _context.Finish();
    }

    private void ThrowIfFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The command context is available only while its handler executes.");
        }
    }
}

/// <summary>Read-only World access and live host services for one event handler.</summary>
/// <remarks>
/// The World view is read-only, but <see cref="GetService{TService}"/> returns the live service
/// instance. Mutating a transactional service from a handler is supported and rolled back with
/// the enclosing transaction; prefer systems and commands for simulation state.
/// </remarks>
public sealed class SystemicEventContext
{
    private readonly SystemicSystemContext _context;
    private bool _finished;

    internal SystemicEventContext(SystemicSystemContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <summary>Gets the logical tick the event is dispatched against.</summary>
    public int Tick
    {
        get
        {
            ThrowIfFinished();
            return _context.Tick;
        }
    }

    /// <summary>Gets the read-only World view for the event.</summary>
    public ReadOnlyWorldView World
    {
        get
        {
            ThrowIfFinished();
            return _context.World;
        }
    }

    /// <summary>Gets a registered host service by its registration type.</summary>
    /// <typeparam name="TService">Service type registered with the game builder.</typeparam>
    public TService GetService<TService>() where TService : notnull
    {
        ThrowIfFinished();
        return _context.GetService<TService>();
    }

    internal void Finish()
    {
        _finished = true;
        _context.Finish();
    }

    private void ThrowIfFinished()
    {
        if (_finished)
        {
            throw new InvalidOperationException("The event context is available only while its handlers execute.");
        }
    }
}

internal interface ICommandHandlerRegistration
{
    Type CommandType { get; }
    void Invoke(ISystemicCommand command, SystemicCommandContext context);
}

internal sealed class CommandHandlerRegistration<TCommand>(ISystemicCommandHandler<TCommand> handler)
    : ICommandHandlerRegistration
    where TCommand : ISystemicCommand
{
    public Type CommandType => typeof(TCommand);

    public void Invoke(ISystemicCommand command, SystemicCommandContext context) =>
        handler.Handle((TCommand)command, context);
}

internal interface IEventInvoker
{
    void Invoke(ISystemicEvent @event, SystemicEventContext context);
}

/// <summary>Handlers registered for one event type, in registration order.</summary>
internal sealed class EventRegistration(Type eventType)
{
    public EventRegistration(Type eventType, IReadOnlyList<IEventInvoker> handlers)
        : this(eventType) => Handlers.AddRange(handlers);

    public Type EventType { get; } = eventType;
    public List<IEventInvoker> Handlers { get; } = [];
}

internal sealed class EventInvoker<TEvent>(ISystemicEventHandler<TEvent> handler) : IEventInvoker
    where TEvent : ISystemicEvent
{
    public void Invoke(ISystemicEvent @event, SystemicEventContext context) =>
        handler.Handle((TEvent)@event, context);
}

internal sealed record UiCommandBinding(
    Type CommandType,
    Func<SystemicUiAction, ISystemicCommand> CreateCommand);

/// <summary>The outcome of one typed command execution.</summary>
/// <param name="Handled">Whether a registered handler ran.</param>
/// <param name="Accepted">Whether the handler accepted the command and the host committed it.</param>
/// <param name="Reason">The handler's rejection reason, or null when accepted.</param>
public readonly record struct SystemicCommandOutcome(bool Handled, bool Accepted, string? Reason)
{
    /// <summary>Gets the outcome of an accepted command.</summary>
    public static SystemicCommandOutcome Succeeded => new(true, true, null);

    /// <summary>Gets the outcome of a command the handler rejected.</summary>
    /// <param name="reason">The rejection reason.</param>
    public static SystemicCommandOutcome Rejected(string reason) => new(true, false, reason);

    /// <summary>Gets the outcome for an action with no bound command.</summary>
    public static SystemicCommandOutcome Unhandled => new(false, false, null);
}

/// <summary>One accepted or rejected typed command, in host execution order.</summary>
/// <param name="Tick">The last completed tick the command ran against.</param>
/// <param name="Sequence">Monotonic position in the host's command journal, continuing after restore.</param>
/// <param name="CommandType">The command's full type name.</param>
/// <param name="Accepted">Whether the command committed, or was rejected and rolled back.</param>
/// <param name="Payload">The command's serialized payload, enough to reconstruct it.</param>
public sealed record SystemicCommandRecord(
    int Tick,
    int Sequence,
    string CommandType,
    bool Accepted,
    JsonElement Payload);
