namespace Systemic.Kernel;

[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class PopulationSchemaAttribute(string id, int version) : Attribute
{
    public string Id { get; } = id;

    public int Version { get; } = version;

    public bool EnableHandles { get; init; }
}

[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class PopulationColumnAttribute(string id) : Attribute
{
    public string Id { get; } = id;

    public bool DoubleBuffered { get; init; }

    public Type? CodecType { get; init; }
}
