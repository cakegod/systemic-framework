namespace Systemic.Extensions;

/// <summary>A behavior extension opts in explicitly and joins the host schedule or save state.</summary>
public interface ISystemicExtensionModule
{
    string Id { get; }

    /// <summary>Gets the composition version of this module's behavior or content.</summary>
    /// <remarks>
    /// Save documents record every module version and refuse to load when one changed, so bump this
    /// when a change makes old state unsafe to reuse. Every module declares a version; the compiler
    /// enforces it, and the save records it.
    /// </remarks>
    int Version { get; }
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

    public IReadOnlyCollection<string> ModuleIds => _modules.Keys;
    public IReadOnlyCollection<ISystemicExtensionModule> Modules => _modules.Values;
}
