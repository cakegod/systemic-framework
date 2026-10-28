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
        var state = new RuleHistoryState([.. rules.CaptureEventHistory()]);
        return JsonSerializer.SerializeToElement(state, SystemicSaveJsonContext.Default.RuleHistoryState);
    }

    public void RestoreState(JsonElement state)
    {
        var restored = state.Deserialize(SystemicSaveJsonContext.Default.RuleHistoryState)
            ?? throw new InvalidOperationException("Saved rule event history is empty.");
        rules.RestoreEventHistory(restored.Samples, currentTick());
    }
}
