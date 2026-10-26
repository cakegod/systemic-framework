namespace Systemic.Rendering;

/// <summary>Write side of one swarm. A system fills a span each simulation tick; hosts read it.</summary>
public sealed class SwarmSource : ISwarmSource
{
    private readonly SwarmInstance[][] _buffers;
    private int _active;
    private int _count;
    private int _pendingCount;

    public SwarmSource(string id, int capacity = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        Id = id;
        _buffers = [new SwarmInstance[capacity], new SwarmInstance[capacity]];
    }

    public string Id { get; }

    public ReadOnlySpan<SwarmInstance> Instances => _buffers[_active].AsSpan(0, _count);

    public int Revision { get; private set; }

    /// <summary>Returns storage for <paramref name="count"/> instances. Call <see cref="EndWrite"/> after writing.</summary>
    public Memory<SwarmInstance> BeginWrite(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var inactive = _active ^ 1;
        if (count > _buffers[inactive].Length)
        {
            _buffers[inactive] = new SwarmInstance[Math.Max(count, Math.Max(4, _buffers[inactive].Length * 2))];
        }

        _pendingCount = count;
        return _buffers[inactive].AsMemory(0, count);
    }

    public void EndWrite()
    {
        _active ^= 1;
        _count = _pendingCount;
        Revision++;
    }
}
