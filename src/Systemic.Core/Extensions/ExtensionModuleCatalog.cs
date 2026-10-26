using Systemic.Kernel;

namespace Systemic.Extensions;

/// <summary>A behavior extension opts in explicitly and registers typed kernel rules.</summary>
public interface ISystemicExtensionModule
{
    string Id { get; }

    void Register(EngineBuilder engine);
}

/// <summary>Deterministic, explicit composition for C# behaviors outside the JSON vocabulary.</summary>
public sealed class ExtensionModuleCatalog
{
    private readonly SortedDictionary<string, ISystemicExtensionModule> _modules = new(StringComparer.Ordinal);

    public ExtensionModuleCatalog Add(ISystemicExtensionModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(module.Id);
        if (!_modules.TryAdd(module.Id, module))
        {
            throw new InvalidOperationException($"Extension module '{module.Id}' is already registered.");
        }

        return this;
    }

    public EventEngine BuildEngine(int maxEffectsPerCommand = EngineLimits.MaxEffectsPerCommand)
    {
        var builder = new EngineBuilder(maxEffectsPerCommand);
        foreach (var module in _modules.Values)
        {
            module.Register(builder);
        }

        return builder.Build();
    }

    public IReadOnlyCollection<string> ModuleIds => _modules.Keys;
    public IReadOnlyCollection<ISystemicExtensionModule> Modules => _modules.Values;
}
