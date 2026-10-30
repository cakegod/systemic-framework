using System.Text.Json;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Save;

internal sealed class RuleHistoryStatefulExtension(RuleEngine rules, Func<int> currentTick)
    : ISaveOnlyStateExtension
{
    public string Id => "systemic.rule-history";

    public JsonElement CaptureState()
    {
        var scheduled = rules.CaptureScheduledFacts();
        var cooldowns = rules.CaptureCooldowns();
        var state = new RuleHistoryState([.. rules.CaptureEventHistory()],
            scheduled.Length == 0 ? null : scheduled,
            cooldowns.Length == 0 ? null : cooldowns);
        return JsonSerializer.SerializeToElement(state, SystemicSaveJsonContext.Default.RuleHistoryState);
    }

    public void RestoreState(JsonElement state)
    {
        var restored = state.Deserialize(SystemicSaveJsonContext.Default.RuleHistoryState)
            ?? throw new InvalidOperationException("Saved rule event history is empty.");
        rules.RestoreEventHistory(restored.Samples, currentTick());
        if (restored.Scheduled is { } scheduled)
        {
            rules.RestoreScheduledFacts(scheduled);
        }

        if (restored.Cooldowns is { } cooldowns)
        {
            rules.RestoreSavedCooldowns(cooldowns);
        }
    }
}
