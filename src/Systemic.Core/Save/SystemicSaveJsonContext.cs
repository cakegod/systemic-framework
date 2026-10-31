using System.Text.Json.Serialization;
using Systemic.Kernel;
using Systemic.Host;

namespace Systemic.Save;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(SaveDocument))]
[JsonSerializable(typeof(SavedWorld))]
[JsonSerializable(typeof(SavedEntity[]))]
[JsonSerializable(typeof(PropertyModifier[]))]
[JsonSerializable(typeof(PropertyGrant[]))]
[JsonSerializable(typeof(DrawStreamSnapshot))]
[JsonSerializable(typeof(RuleHistoryState))]
[JsonSerializable(typeof(Systemic.Host.SystemicCommandRecord[]))]
[JsonSerializable(typeof(PopulationWorldState))]
[JsonSerializable(typeof(FixedStepClockState))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(Dictionary<string, System.Text.Json.JsonElement>))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
internal partial class SystemicSaveJsonContext : JsonSerializerContext;
