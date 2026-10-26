namespace Systemic.Kernel;

internal interface IWorldStateAttachment
{
    void AttachToWorld(World world);

    void DetachFromWorld(World world);

    void CopyDetachedStateTo(IWorldStateAttachment clone);
}

internal interface IColumnUndoBatch
{
    void Undo();

    void Release();
}

internal sealed class EntityColumn<TEntity, TState>
    where TEntity : Entity
    where TState : struct
{
    private TState[] _values = new TState[16];
    private readonly Stack<ColumnUndoBatch> _freeUndoBatches = new();

    public ref TState ById(int entityId)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entityId);
        if ((uint)entityId >= (uint)_values.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(entityId), entityId,
                "The entity has not been attached to this column.");
        }

        return ref _values[entityId];
    }

    public void Set(int entityId, TState value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(entityId);
        if ((uint)entityId >= (uint)_values.Length)
        {
            Array.Resize(ref _values, Math.Max(entityId + 1, _values.Length * 2));
        }

        _values[entityId] = value;
    }

    public void Clear(int entityId)
    {
        if ((uint)entityId < (uint)_values.Length)
        {
            _values[entityId] = default;
        }
    }

    public ColumnUndoBatch RentUndoBatch() =>
        _freeUndoBatches.Count > 0 ? _freeUndoBatches.Pop() : new ColumnUndoBatch(this);

    internal sealed class ColumnUndoBatch(EntityColumn<TEntity, TState> column) : IColumnUndoBatch
    {
        private readonly List<(int EntityId, TState State)> _rows = [];

        public void Capture(int entityId, TState state) => _rows.Add((entityId, state));

        public void Undo()
        {
            for (var index = _rows.Count - 1; index >= 0; index--)
            {
                var (entityId, state) = _rows[index];
                column.Set(entityId, state);
            }
        }

        public void Release()
        {
            _rows.Clear();
            column._freeUndoBatches.Push(this);
        }
    }
}

internal readonly struct ColumnMutationBatch<TEntity, TState>(
    World world,
    EntityColumn<TEntity, TState> column,
    EntityColumn<TEntity, TState>.ColumnUndoBatch? undoBatch)
    where TEntity : Entity
    where TState : struct
{
    public void Set(TEntity entity, TState value)
    {
        if (!ReferenceEquals(entity.OwnerWorld, world))
        {
            throw new InvalidOperationException("Column mutations require an entity attached to the owning world.");
        }

        ref var current = ref column.ById(entity.Id);
        if (EqualityComparer<TState>.Default.Equals(current, value))
        {
            return;
        }

        undoBatch?.Capture(entity.Id, current);
        current = value;
    }
}
