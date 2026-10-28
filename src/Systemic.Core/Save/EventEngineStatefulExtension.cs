using System.Text.Json;
using Systemic.Host;
using Systemic.Kernel;

namespace Systemic.Save;

/// <summary>Persists the event engine's counters so event IDs continue identically after a load.</summary>
internal sealed class EventEngineStatefulExtension(EventEngine engine) : ISaveOnlyStateExtension
{
    public const string ExtensionId = "systemic.events";
    public string Id => ExtensionId;

    public JsonElement CaptureState() => JsonSerializer.SerializeToElement(
        new EventEngineState(engine.Serial, engine.ChainId),
        SystemicSaveJsonContext.Default.EventEngineState);

    public void RestoreState(JsonElement state)
    {
        try
        {
            var restored = state.Deserialize(SystemicSaveJsonContext.Default.EventEngineState)
                ?? throw new InvalidOperationException("Saved event engine state is empty.");
            engine.RestoreCounters(restored.Serial, restored.ChainId);
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            throw new SystemicSaveException($"Could not restore event engine state: {exception.Message}", exception);
        }
    }
}

internal sealed record EventEngineState(long Serial, int ChainId);
