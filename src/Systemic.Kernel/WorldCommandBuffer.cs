namespace Systemic.Kernel;

public readonly struct WorldCommandBuffer
{
    private readonly WorldCommandStorage? _storage;
    private readonly long _generation;

    public WorldCommandBuffer() : this(new WorldCommandStorage(), 0)
    {
    }

    internal WorldCommandBuffer(WorldCommandStorage storage, long generation)
    {
        _storage = storage;
        _generation = generation;
    }

    private WorldCommandStorage Storage
    {
        get
        {
            var storage = _storage ?? throw new InvalidOperationException("The command buffer is uninitialized.");
            storage.ValidateWriter(_generation);
            return storage;
        }
    }

    public int Count => Storage.Count;
    public void Reserve(int commandCapacity) => Storage.Reserve(commandCapacity);
    public void SetProperty(int entityId, string property, double value) => Storage.SetProperty(entityId, property, value);
    public void Add(Entity entity) => Storage.Add(entity);
    public void Remove(int entityId) => Storage.Remove(entityId);
    public void AddTag(int entityId, string tag) => Storage.AddTag(entityId, tag);
    public void RemoveTag(int entityId, string tag) => Storage.RemoveTag(entityId, tag);
    public void AddCapability(int entityId, string capability) => Storage.AddCapability(entityId, capability);
    public void RemoveCapability(int entityId, string capability) => Storage.RemoveCapability(entityId, capability);
    public void SetLink(int entityId, string link, int targetEntityId) => Storage.SetLink(entityId, link, targetEntityId);
    public void RemoveLink(int entityId, string link) => Storage.RemoveLink(entityId, link);

    public void SetModifier(
        string source,
        int entityId,
        string property,
        ModifierOperation operation,
        double value,
        int remainingTicks = -1,
        int priority = 0,
        ModifierStage stage = ModifierStage.Standard) =>
        Storage.SetModifier(source, entityId, property, operation, value, remainingTicks, priority, stage);

    public void RemoveModifier(string source, int entityId, string property) =>
        Storage.RemoveModifier(source, entityId, property);

    public void Grant(string source, int entityId, GrantKind kind, string name, int remainingTicks = -1) =>
        Storage.Grant(source, entityId, kind, name, remainingTicks);

    public void Revoke(string source, int entityId, GrantKind kind, string name) =>
        Storage.Revoke(source, entityId, kind, name);

    public void SetSpatialBounds(int entityId, Bounds2 bounds) => Storage.SetSpatialBounds(entityId, bounds);
    public void RemoveSpatialBounds(int entityId) => Storage.RemoveSpatialBounds(entityId);
    public void Clear() => Storage.Clear();
    public void Flush(World world) => Storage.Flush(world);
}
