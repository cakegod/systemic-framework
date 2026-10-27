using System.Text.Json;
using Systemic.Host;

namespace Systemic.Save;

internal sealed class ClockStatefulExtension(FixedStepClock clock) : ISystemicStatefulExtension
{
    public const string ExtensionId = "systemic.clock";
    public string Id => ExtensionId;

    public JsonElement CaptureState() => JsonSerializer.SerializeToElement(clock.CaptureState(),
        SystemicSaveJsonContext.Default.FixedStepClockState);

    public void RestoreState(JsonElement state)
    {
        try
        {
            clock.RestoreState(state.Deserialize(SystemicSaveJsonContext.Default.FixedStepClockState));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new SystemicSaveException($"Could not restore clock state: {exception.Message}", exception);
        }
    }
}
