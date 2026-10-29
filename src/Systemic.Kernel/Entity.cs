using System.Collections.Immutable;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Systemic.Kernel;

internal readonly struct EntityPropertyUndo(long bits, object? reference)
{
    private readonly long _bits = bits;
    private readonly object? _reference = reference;

    public static EntityPropertyUndo Capture<T>(T value)
    {
        var type = typeof(T);
        if (type == typeof(double))
        {
            return new(BitConverter.DoubleToInt64Bits(Unsafe.As<T, double>(ref value)), null);
        }

        if (type == typeof(float))
        {
            return new(BitConverter.SingleToInt32Bits(Unsafe.As<T, float>(ref value)), null);
        }

        if (type == typeof(long))
        {
            return new(Unsafe.As<T, long>(ref value), null);
        }

        if (type == typeof(ulong))
        {
            return new(unchecked((long)Unsafe.As<T, ulong>(ref value)), null);
        }

        if (type == typeof(int))
        {
            return new(Unsafe.As<T, int>(ref value), null);
        }

        if (type == typeof(uint))
        {
            return new(Unsafe.As<T, uint>(ref value), null);
        }

        if (type == typeof(short))
        {
            return new(Unsafe.As<T, short>(ref value), null);
        }

        if (type == typeof(ushort))
        {
            return new(Unsafe.As<T, ushort>(ref value), null);
        }

        if (type == typeof(byte))
        {
            return new(Unsafe.As<T, byte>(ref value), null);
        }

        if (type == typeof(sbyte))
        {
            return new(Unsafe.As<T, sbyte>(ref value), null);
        }

        if (type == typeof(char))
        {
            return new(Unsafe.As<T, char>(ref value), null);
        }

        if (type == typeof(bool))
        {
            return new(Unsafe.As<T, bool>(ref value) ? 1 : 0, null);
        }

        if (type.IsEnum)
        {
            return new(CaptureEnum(ref value, Enum.GetUnderlyingType(type)), null);
        }

        return new(0, value);
    }

    public object? ToObject(Type type)
    {
        if (type == typeof(double))
        {
            return BitConverter.Int64BitsToDouble(_bits);
        }

        if (type == typeof(float))
        {
            return BitConverter.Int32BitsToSingle((int)_bits);
        }

        if (type == typeof(long))
        {
            return _bits;
        }

        if (type == typeof(ulong))
        {
            return unchecked((ulong)_bits);
        }

        if (type == typeof(int))
        {
            return (int)_bits;
        }

        if (type == typeof(uint))
        {
            return (uint)_bits;
        }

        if (type == typeof(short))
        {
            return (short)_bits;
        }

        if (type == typeof(ushort))
        {
            return (ushort)_bits;
        }

        if (type == typeof(byte))
        {
            return (byte)_bits;
        }

        if (type == typeof(sbyte))
        {
            return (sbyte)_bits;
        }

        if (type == typeof(char))
        {
            return (char)_bits;
        }

        if (type == typeof(bool))
        {
            return _bits != 0;
        }

        if (type.IsEnum)
        {
            var underlyingType = Enum.GetUnderlyingType(type);
            object value = underlyingType == typeof(ulong) ? unchecked((ulong)_bits)
                : underlyingType == typeof(long) ? _bits
                : underlyingType == typeof(uint) ? (uint)_bits
                : underlyingType == typeof(int) ? (int)_bits
                : underlyingType == typeof(ushort) ? (ushort)_bits
                : underlyingType == typeof(short) ? (short)_bits
                : underlyingType == typeof(byte) ? (byte)_bits
                : (sbyte)_bits;

            return Enum.ToObject(type, value);
        }

        return _reference;
    }

    private static long CaptureEnum<T>(ref T value, Type underlyingType) =>
        underlyingType == typeof(ulong) ? unchecked((long)Unsafe.As<T, ulong>(ref value))
        : underlyingType == typeof(long) ? Unsafe.As<T, long>(ref value)
        : underlyingType == typeof(uint) ? Unsafe.As<T, uint>(ref value)
        : underlyingType == typeof(int) ? Unsafe.As<T, int>(ref value)
        : underlyingType == typeof(ushort) ? Unsafe.As<T, ushort>(ref value)
        : underlyingType == typeof(short) ? Unsafe.As<T, short>(ref value)
        : underlyingType == typeof(byte) ? Unsafe.As<T, byte>(ref value)
        : Unsafe.As<T, sbyte>(ref value);
}

public static class EntityTags
{
    public const string Resource = "resource";
}

public static class EntityCapabilities
{
    public const string BeProduced = "be_produced";
    public const string BeSpent = "be_spent";
}

public static class EntityProperties
{
    public const string Amount = "amount";
}

public abstract class Entity
{
    public const string ToRemoveProperty = "to_remove";

    private static readonly ConcurrentDictionary<(Type Type, string Property), PropertyInfo> JournalProperties = [];

    private ImmutableHashSet<string> _tags = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private ImmutableHashSet<string> _capabilities = ImmutableHashSet.Create<string>(StringComparer.Ordinal);
    private ImmutableDictionary<string, int> _links =
        ImmutableDictionary.Create<string, int>(StringComparer.Ordinal);

    protected Entity(params string[] tags)
    {
        foreach (var tag in tags)
        {
            AddTag(tag);
        }
    }

    public int Id { get; internal set; }

    public World? OwnerWorld { get; internal set; }

    internal long StructuralVersion { get; private set; }

    internal void MarkStructureChanged() => StructuralVersion++;

    protected internal virtual bool SupportsCompactPropertyUndo => false;

    /// <summary>Gets whether this entity is marked for removal at the next prune.</summary>
    /// <remarks>
    /// The flag is owned by the World: callers mark an entity with <see cref="World.Remove"/> or
    /// queue a command with <see cref="WorldCommandBuffer.Remove"/>, so a pending removal is part of
    /// the world's journalled state instead of a write behind its back.
    /// </remarks>
    public bool ToRemove
    {
        get;
        internal set
        {
            if (field == value)
            {
                return;
            }

            Set(ref field, value);
            OwnerWorld?.UpdateRemovalCandidate(this);
            OwnerWorld?.MarkPopulationDirty();
            OwnerWorld?.MarkFirstTaggedDirty();
        }
    }

    public IReadOnlySet<string> Tags => _tags;

    public IReadOnlySet<string> Capabilities => _capabilities;

    public IReadOnlyDictionary<string, int> Links => _links;

    public bool HasTag(string tag) => Tags.Contains(tag);

    public bool HasCapability(string capability) => Capabilities.Contains(capability);

    /// <summary>Restores the entity's portable identity fields before it is attached to a world.</summary>
    public void RestoreBaseState(
        IEnumerable<string> tags,
        IEnumerable<string> capabilities,
        IEnumerable<KeyValuePair<string, int>> links,
        bool toRemove)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(capabilities);
        ArgumentNullException.ThrowIfNull(links);
        if (OwnerWorld is not null)
        {
            throw new InvalidOperationException("Base state can only be restored before an entity is attached to a world.");
        }

        _tags = tags.ToImmutableHashSet(StringComparer.Ordinal);
        _capabilities = capabilities.ToImmutableHashSet(StringComparer.Ordinal);
        _links = links.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        ToRemove = toRemove;
    }

    public void AddTag(string tag)
    {
        MarkMutated();
        if (_tags.Contains(tag))
        {
            return;
        }

        _tags = _tags.Add(tag);
        OwnerWorld?.UpdateMembershipIndex(this, GrantKind.Tag, tag, present: true);
    }

    public void RemoveTag(string tag)
    {
        MarkMutated();
        if (!_tags.Contains(tag))
        {
            return;
        }

        _tags = _tags.Remove(tag);
        if (OwnerWorld is { } world)
        {
            world.UpdateMembershipIndex(this, GrantKind.Tag, tag, present: world.HasTag(this, tag));
        }
    }

    public void AddCapability(string capability)
    {
        MarkMutated();
        if (_capabilities.Contains(capability))
        {
            return;
        }

        _capabilities = _capabilities.Add(capability);
        OwnerWorld?.UpdateMembershipIndex(this, GrantKind.Capability, capability, present: true);
    }

    public void RemoveCapability(string capability)
    {
        MarkMutated();
        if (!_capabilities.Contains(capability))
        {
            return;
        }

        _capabilities = _capabilities.Remove(capability);
        if (OwnerWorld is { } world)
        {
            world.UpdateMembershipIndex(this, GrantKind.Capability, capability,
                present: world.HasCapability(this, capability));
        }
    }

    public void SetLink(string link, int targetId)
    {
        MarkMutated();
        var previous = _links.TryGetValue(link, out var previousTarget) ? (int?)previousTarget : null;
        _links = _links.SetItem(link, targetId);
        OwnerWorld?.UpdateLinkIndex(this, link, previous, targetId);
    }

    public void RemoveLink(string link)
    {
        MarkMutated();
        var previous = _links.TryGetValue(link, out var previousTarget) ? (int?)previousTarget : null;
        _links = _links.Remove(link);
        OwnerWorld?.UpdateLinkIndex(this, link, previous, null);
    }

    public virtual bool TrySetProperty(World world, string name, double value) => false;

    public virtual double TryProduce(World world, double amount, int activations = 1) => 0.0;

    public virtual bool TryGetProperty(World world, string name, out double value)
    {
        if (name == ToRemoveProperty)
        {
            value = ToRemove ? 1 : 0;

            return true;
        }

        value = 0;

        return false;
    }

    public abstract Entity Clone();

    public abstract void RestoreFrom(Entity snapshot);

    protected void RestoreBaseFrom(Entity snapshot)
    {
        MarkMutated();
        var world = OwnerWorld;
        var previousTags = _tags;
        var previousCapabilities = _capabilities;
        var previousLinks = _links;
        var tagsChanged = !ReferenceEquals(previousTags, snapshot._tags);
        var capabilitiesChanged = !ReferenceEquals(previousCapabilities, snapshot._capabilities);
        ToRemove = snapshot.ToRemove;
        _tags = snapshot._tags;
        _capabilities = snapshot._capabilities;
        _links = snapshot._links;

        if (world is null)
        {
            return;
        }

        if (!ReferenceEquals(previousLinks, _links))
        {
            foreach (var (link, target) in previousLinks)
            {
                world.UpdateLinkIndex(this, link, target,
                    _links.TryGetValue(link, out var nextTarget) ? nextTarget : null);
            }

            foreach (var (link, target) in _links)
            {
                if (!previousLinks.ContainsKey(link))
                {
                    world.UpdateLinkIndex(this, link, null, target);
                }
            }
        }

        if (tagsChanged)
        {
            world.MarkPopulationDirty();
            foreach (var tag in previousTags.Except(_tags))
            {
                world.UpdateMembershipIndex(this, GrantKind.Tag, tag, world.HasTag(this, tag));
            }

            foreach (var tag in _tags.Except(previousTags))
            {
                world.UpdateMembershipIndex(this, GrantKind.Tag, tag, world.HasTag(this, tag));
            }
        }

        if (capabilitiesChanged)
        {
            foreach (var capability in previousCapabilities.Except(_capabilities))
            {
                world.UpdateMembershipIndex(this, GrantKind.Capability, capability,
                    world.HasCapability(this, capability));
            }

            foreach (var capability in _capabilities.Except(previousCapabilities))
            {
                world.UpdateMembershipIndex(this, GrantKind.Capability, capability,
                    world.HasCapability(this, capability));
            }
        }
    }

    internal void RestoreJournaledProperty(string propertyName, EntityPropertyUndo undo)
    {
        var property = JournalProperties.GetOrAdd((GetType(), propertyName), static key =>
            key.Type.GetProperty(key.Property,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                $"Entity property '{key.Property}' is missing from {key.Type.Name}."));
        property.SetValue(this, undo.ToObject(property.PropertyType));
    }

    protected void CopyBaseTo(Entity clone)
    {
        clone.Id = Id;
        clone.OwnerWorld = null;
        clone.ToRemove = ToRemove;
        clone._tags = _tags;
        clone._capabilities = _capabilities;
        clone._links = _links;
        if (this is IWorldStateAttachment sourceState
            && clone is IWorldStateAttachment cloneState)
        {
            sourceState.CopyDetachedStateTo(cloneState);
        }
    }

    // WHY: a write must snapshot before it lands, so mutators mark before they assign. Mutation must not depend on callers remembering World.Edit.
    protected void Set<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        RecordCompactPropertyUndo(propertyName, field);

        field = value;
    }

    protected void RecordCompactPropertyUndo<T>(string propertyName, T previousValue)
    {
        if (propertyName.Length == 0 || !SupportsCompactPropertyUndo)
        {
            MarkMutated();

            return;
        }

        OwnerWorld?.RecordPropertyMutation(this, propertyName, EntityPropertyUndo.Capture(previousValue));
    }

    protected void MarkMutated() => OwnerWorld?.MarkMutated(this);

    protected static void RestoreArray<T>(ref T[] live, T[] snapshot)
    {
        if (ReferenceEquals(live, snapshot))
        {
            return;
        }

        if (live.Length == snapshot.Length)
        {
            snapshot.CopyTo(live, 0);

            return;
        }

        live = snapshot;
    }

    protected void SetElement<T>(T[] array, int index, T value)
    {
        if (EqualityComparer<T>.Default.Equals(array[index], value))
        {
            return;
        }

        MarkMutated();
        array[index] = value;
    }
}

public abstract class ResourceEntity : Entity
{
    public double Amount { get; protected set => Set(ref field, value); }
    public double Capacity { get; }

    protected ResourceEntity(double capacity, params string[] tags)
        : base([EntityTags.Resource, .. tags])
    {
        Capacity = capacity;
        AddCapability(EntityCapabilities.BeProduced);
    }
    public abstract string Identity { get; }

    public bool TrySpend(World world, double amount)
    {
        if (!world.HasCapability(this, EntityCapabilities.BeSpent)
            || !double.IsFinite(amount)
            || !double.IsFinite(Amount)
            || amount < 0.0
            || Amount < amount)
        {
            return false;
        }

        Amount -= amount;

        return true;
    }

    public double TryAdd(double amount)
    {
        if (!double.IsFinite(amount) || !double.IsFinite(Amount))
        {
            return 0.0;
        }

        var accepted = Math.Min(Math.Max(amount, 0.0), Math.Max(Capacity - Amount, 0.0));
        var next = Amount + accepted;
        if (!double.IsFinite(next))
        {
            return 0.0;
        }

        Amount = next;

        return accepted;
    }

    public double TrySpendUpTo(World world, double amount)
    {
        if (!world.HasCapability(this, EntityCapabilities.BeSpent)
            || !double.IsFinite(amount)
            || !double.IsFinite(Amount)
            || amount < 0.0)
        {
            return 0.0;
        }

        var paid = Math.Min(Amount, amount);
        Amount -= paid;

        return paid;
    }

    public bool TrySpendAll(World world)
    {
        if (!world.HasCapability(this, EntityCapabilities.BeSpent))
        {
            return false;
        }

        Amount = 0.0;

        return true;
    }

    public bool CanProduce(World world, double amount, out double accepted)
    {
        accepted = 0.0;
        if (!double.IsFinite(amount) || !double.IsFinite(Amount))
        {
            return false;
        }

        var requested = amount * world.ValueScale;
        if (!double.IsFinite(requested))
        {
            return false;
        }

        accepted = Math.Min(Math.Max(requested, 0.0), Math.Max(Capacity - Amount, 0.0));

        return double.IsFinite(Amount + accepted) && CanAcceptProduction(accepted);
    }

    public override double TryProduce(World world, double amount, int activations = 1)
    {
        if (!CanProduce(world, amount, out var accepted))
        {
            return 0.0;
        }

        MarkMutated();
        Amount += accepted;
        OnProduced(world, accepted, activations);

        return accepted;
    }

    protected virtual bool CanAcceptProduction(double amount) => true;

    protected virtual void OnProduced(World world, double amount, int activations)
    {
    }

    public override bool TryGetProperty(World world, string name, out double value)
    {
        if (name == EntityProperties.Amount)
        {
            value = Amount;

            return true;
        }

        return base.TryGetProperty(world, name, out value);
    }

    public abstract override void RestoreFrom(Entity snapshot);

    protected void RestoreResourceFrom(Entity snapshot)
    {
        RestoreBaseFrom(snapshot);
        Amount = ((ResourceEntity)snapshot).Amount;
    }
}
