using System.Text.Json;
using Systemic.Kernel;
using Systemic.Host;

namespace Systemic.Save;

internal sealed class PopulationStatefulExtension(World world) : ISystemicStatefulExtension
{
    public const string ExtensionId = "systemic.populations";

    public string Id => ExtensionId;

    public JsonElement CaptureState()
    {
        try
        {
            return JsonSerializer.SerializeToElement(world.CapturePopulationState(),
                SystemicSaveJsonContext.Default.PopulationWorldState);
        }
        catch (Exception exception) when (exception is not SystemicSaveException)
        {
            throw new SystemicSaveException("Could not capture population state.", exception);
        }
    }

    public void RestoreState(JsonElement state)
    {
        try
        {
            var populationState = state.Deserialize(SystemicSaveJsonContext.Default.PopulationWorldState)
                ?? throw new SystemicSaveException("Saved population state is empty.");
            world.RestorePopulationState(populationState);
        }
        catch (SystemicSaveException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException
            or FormatException or InvalidDataException or InvalidOperationException or OverflowException)
        {
            throw new SystemicSaveException($"Could not restore population state: {exception.Message}", exception);
        }
    }
}
