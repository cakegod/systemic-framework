using System.Text.Json.Nodes;

namespace Systemic.Save;

/// <summary>
/// Drops the retired typed event engine's counters. Version 6 has one dispatch path, so the
/// extension no longer exists and a leftover entry would fail the load as an unregistered extension.
/// </summary>
internal sealed class RetiredEventEngineStateMigration : ISaveMigration
{
    public const string ExtensionId = "systemic.events";

    public int FromVersion => 5;

    public void Apply(JsonObject document)
    {
        if (document["extensionStates"] is JsonObject extensionStates)
        {
            extensionStates.Remove(ExtensionId);
        }
    }
}
