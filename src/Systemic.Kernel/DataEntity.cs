using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Systemic.Kernel;

public sealed class DataEntity : Entity
{
    private readonly Dictionary<string, double> _properties = new(StringComparer.Ordinal);
    private readonly ReadOnlyDictionary<string, double> _propertiesView;

    public DataEntity(
        IEnumerable<string> tags,
        IEnumerable<string> capabilities,
        params (string Name, double Value)[] properties)
        : base([.. tags])
    {
        _propertiesView = new ReadOnlyDictionary<string, double>(_properties);
        foreach (var capability in capabilities)
        {
            AddCapability(capability);
        }

        foreach (var (name, value) in properties)
        {
            _properties[name] = value;
        }
    }

    public IReadOnlyDictionary<string, double> Properties => _propertiesView;

    public override bool TryGetProperty(World world, string name, out double value)
    {
        if (_properties.TryGetValue(name, out value))
        {
            return true;
        }

        return base.TryGetProperty(world, name, out value);
    }

    public override bool TrySetProperty(World world, string name, double value)
    {
        if (!_properties.TryGetValue(name, out double value1))
        {
            return base.TrySetProperty(world, name, value);
        }

        world.RecordDataEntityPropertyMutation(this, name, value1);
        _properties[name] = value;

        return true;
    }

    // Rule adjustments can update the existing dictionary slot after recording its undo value.
    internal bool TryAdjustProperty(World world, string name, double amount, out double updated)
    {
        ref var current = ref CollectionsMarshal.GetValueRefOrNullRef(_properties, name);
        if (Unsafe.IsNullRef(ref current))
        {
            updated = 0;
            return false;
        }

        var previous = current;
        updated = previous + amount;
        if (double.IsFinite(updated))
        {
            world.RecordDataEntityPropertyMutation(this, name, previous);
            current = updated;
        }

        return true;
    }

    public override Entity Clone()
    {
        var clone = new DataEntity([], []);
        CopyBaseTo(clone);
        CopyPropertiesTo(clone);

        return clone;
    }

    public override void RestoreFrom(Entity snapshot)
    {
        RestoreBaseFrom(snapshot);
        var source = (DataEntity)snapshot;
        _properties.Clear();
        foreach (var (name, value) in source._properties)
        {
            _properties[name] = value;
        }
    }

    internal void RestoreDataProperty(string name, double value) => _properties[name] = value;

    private void CopyPropertiesTo(DataEntity clone)
    {
        foreach (var (name, value) in _properties)
        {
            clone._properties[name] = value;
        }
    }
}

public sealed class SimpleResourceEntity(string identity, double capacity, params string[] tags)
    : ResourceEntity(capacity, tags)
{
    public override string Identity => identity;

    public override Entity Clone()
    {
        var clone = new SimpleResourceEntity(identity, Capacity) { Amount = Amount };
        CopyBaseTo(clone);

        return clone;
    }

    public override void RestoreFrom(Entity snapshot) => RestoreResourceFrom(snapshot);
}
