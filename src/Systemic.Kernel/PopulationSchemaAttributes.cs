namespace Systemic.Kernel;

/// <summary>Marks an unmanaged partial struct for generated population bindings.</summary>
/// <remarks>The schema ID and version are persisted with the population and must remain stable across compatible builds.</remarks>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class PopulationSchemaAttribute(string id, int version) : Attribute
{
    /// <summary>Gets the stable identifier for this population schema.</summary>
    public string Id { get; } = id;

    /// <summary>Gets the schema version written to population state.</summary>
    public int Version { get; } = version;

    /// <summary>Gets or sets whether the population exposes stable entity handles.</summary>
    public bool EnableHandles { get; init; }
}

/// <summary>Defines a stable column and its serialization settings in a population schema.</summary>
[AttributeUsage(AttributeTargets.Field, Inherited = false)]
public sealed class PopulationColumnAttribute(string id) : Attribute
{
    /// <summary>Gets the stable identifier for the column.</summary>
    public string Id { get; } = id;

    /// <summary>Gets or sets whether updates write to a separate next buffer.</summary>
    public bool DoubleBuffered { get; init; }

    /// <summary>Gets or sets an explicit codec type for this column.</summary>
    public Type? CodecType { get; init; }
}
