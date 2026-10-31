using Systemic.Host;

namespace ModulePack;

/// <summary>A game module is the composition root: behavior lives in C#, the pack carries data.</summary>
/// <remarks>
/// The pack ships no <c>rules.json</c>. Everything this game does is composed here, so deleting the
/// legacy rule engine leaves the sample untouched.
/// </remarks>
public sealed class ForgeModule(int version = 1) : ISystemicGameModule
{
    public string Id => "sample.module-pack.forge";

    public int Version => version;

    public void Compose(SystemicGameBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder
            .AddSystem(new ForgePulseSystem())
            .AddCommand<StokeCommand, StokeHandler>();
    }
}

/// <summary>Stokes the forge by an amount the player chose.</summary>
/// <param name="EntityId">The forge entity the action targets.</param>
/// <param name="Amount">How much heat to add.</param>
public sealed record StokeCommand(int EntityId, int Amount) : ISystemicCommand;

/// <summary>Writes the stoked heat in one transaction, or rejects a non-positive amount.</summary>
public sealed class StokeHandler : ISystemicCommandHandler<StokeCommand>
{
    public void Handle(StokeCommand command, SystemicCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (command.Amount <= 0)
        {
            context.Reject("stoke.nonpositive");
            return;
        }

        var heat = context.World.TryGetProperty(command.EntityId, "heat", out var current) ? current : 0;
        context.Commands.SetProperty(command.EntityId, "heat", heat + command.Amount);
    }
}

/// <summary>Emits one typed event per tick, so other systems and observers can react in order.</summary>
public sealed class ForgePulseSystem : ISystemicSystem
{
    public string Id => "sample.module-pack.pulse";

    public int Order => 0;

    public void Step(SystemicSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.EmitEvent(new ForgePulsed(context.Tick));
    }
}

/// <summary>A typed tick notification owned by the sample module, not by content.</summary>
/// <param name="Tick">The tick that emitted the event.</param>
public sealed record ForgePulsed(int Tick) : ISystemicEvent;

/// <summary>Counts pulses, standing in for whatever a game would do with them.</summary>
public sealed class PulseCounter : ISystemicEventHandler<ForgePulsed>
{
    public int Count { get; private set; }

    public void Handle(ForgePulsed @event, SystemicEventContext context)
    {
        ArgumentNullException.ThrowIfNull(@event);
        Count++;
    }
}
