using Systemic.Kernel;
using Systemic.Host;

namespace Systemic.Rules;

internal sealed class ScheduledEventSystem : ISystemicSystem
{
    private readonly World _world;
    private readonly ScheduledFact[] _facts;

    public ScheduledEventSystem(
        IReadOnlyList<RuleScheduleDefinition> schedules,
        World world,
        IReadOnlyDictionary<string, int> entityIds)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(entityIds);
        _world = world;
        _facts = [.. schedules.Select(schedule => new ScheduledFact(
            schedule,
            ResolveEntityId(schedule.SourceEntityId),
            ResolveEntityId(schedule.TargetEntityId)))];

        int ResolveEntityId(string? contentId)
        {
            if (contentId is null)
            {
                return 0;
            }

            if (!entityIds.TryGetValue(contentId, out var id) || world.ById(id) is null)
            {
                throw new InvalidOperationException($"Schedule references missing entity '{contentId}'.");
            }

            return id;
        }
    }

    public string Id => "systemic.schedules";

    public int Order => -100;

    public void Step(SystemicStepContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ReferenceEquals(context.World, _world))
        {
            throw new InvalidOperationException("Schedule system cannot step a different world than the one it was initialized with.");
        }

        foreach (var (schedule, sourceId, targetId) in _facts)
        {
            if (context.Tick < schedule.OffsetTicks
                || (context.Tick - schedule.OffsetTicks) % schedule.EveryTicks != 0)
            {
                continue;
            }

            context.Publish(new SimulationFact(schedule.EventType, sourceId, targetId, Amount: schedule.Amount));
        }
    }

    private readonly record struct ScheduledFact(RuleScheduleDefinition Schedule, int SourceId, int TargetId);
}
