using System.Collections.Immutable;
using Systemic.Kernel;
using Systemic.Rules;

namespace Systemic.Ui;

public sealed record SystemicUiBindingContext(
    World World,
    ImmutableDictionary<string, int> EntityIds,
    ImmutableDictionary<string, double> Selection,
    SimulationFact? EventFact,
    int? OwnerEntityId);

public readonly record struct SystemicUiBindingValue(bool HasValue, double? Number, string? Text)
{
    public static SystemicUiBindingValue FromNumber(double value) => new(true, value, null);
    public static SystemicUiBindingValue FromText(string value) => new(true, null, value);
}

/// <summary>Reports the actions declared by a loaded UI module so bindings can be validated at build.</summary>
public interface ISystemicUiActionCatalog
{
    /// <summary>Checks whether any layout in this module declares the action.</summary>
    /// <param name="actionId">The action ID a game bound to a command.</param>
    bool DeclaresAction(string actionId);
}

/// <summary>Provides values for module-owned declarative UI bindings without a module reference in Core.</summary>
public interface ISystemicUiBindingProvider
{
    bool TryResolve(string binding, SystemicUiBindingContext context, out SystemicUiBindingValue value);

    string? ResolveVisualState(string binding, SystemicUiBindingContext context) => null;
}
