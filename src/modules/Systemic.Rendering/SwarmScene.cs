namespace Systemic.Rendering;

/// <summary>All swarms a host draws in one frame.</summary>
public sealed class SwarmScene
{
    private readonly List<ISwarmSource> _sources = [];

    public IReadOnlyList<ISwarmSource> Sources => _sources;

    public int InstanceCount
    {
        get
        {
            var count = 0;
            for (var index = 0; index < _sources.Count; index++)
            {
                count += _sources[index].Instances.Length;
            }

            return count;
        }
    }

    public void Add(ISwarmSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var existing in _sources)
        {
            if (existing.Id == source.Id)
            {
                throw new ArgumentException($"Swarm source '{source.Id}' is already part of the scene.", nameof(source));
            }
        }

        _sources.Add(source);
    }

    public bool Remove(ISwarmSource source) => _sources.Remove(source);
}
