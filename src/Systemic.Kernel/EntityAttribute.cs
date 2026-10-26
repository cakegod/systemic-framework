namespace Systemic.Kernel;

// WHY: the entity generator needs one opt-in marker; inferring intent from `sealed partial class : Entity` would also capture test fixtures and hand-written kernel entities.
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EntityAttribute(Type? properties = null) : Attribute
{
    public Type? Properties { get; } = properties;
}
