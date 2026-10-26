namespace Systemic.Kernel;

internal interface IEntityQuery
{
    string[] Tags { get; }

    string[] Capabilities { get; }

    LinkRequirement[] Links { get; }

    bool Cacheable { get; }

    bool Matches(World world, Entity entity);
}

/// <summary>A typed entity query backed by the world's stable tag and capability indexes.</summary>
public sealed class EntityQuery<TEntity>(
    string[]? tags = null,
    string[]? capabilities = null,
    Func<World, TEntity, bool>? predicate = null,
    LinkRequirement[]? links = null) : IEntityQuery
    where TEntity : Entity
{
    private readonly Func<World, TEntity, bool>? _predicate = predicate;

    internal string[] Tags { get; } = tags is null ? [] : [.. tags];

    internal string[] Capabilities { get; } = capabilities is null ? [] : [.. capabilities];

    private readonly LinkRequirement[] _links = links is null ? [] : [.. links];

    LinkRequirement[] IEntityQuery.Links => _links;

    bool IEntityQuery.Cacheable => _predicate is null;

    string[] IEntityQuery.Tags => Tags;

    string[] IEntityQuery.Capabilities => Capabilities;

    bool IEntityQuery.Matches(World world, Entity entity) =>
        entity is TEntity typed && Matches(world, typed);

    internal bool Matches(World world, TEntity entity)
    {
        foreach (var tag in Tags)
        {
            if (!world.HasTag(entity, tag))
            {
                return false;
            }
        }

        foreach (var capability in Capabilities)
        {
            if (!world.HasCapability(entity, capability))
            {
                return false;
            }
        }

        foreach (var requirement in _links)
        {
            if (!entity.Links.TryGetValue(requirement.Link, out var id)
                || world.ById(id) is not { } linked
                || !world.HasTag(linked, requirement.Tag)
                || (requirement.Capability.Length != 0
                    && !world.HasCapability(linked, requirement.Capability)))
            {
                return false;
            }
        }

        return _predicate?.Invoke(world, entity) ?? true;
    }
}

internal readonly record struct EntityQuerySnapshot(
    ReadOnlyMemory<int> Ids,
    ReadOnlyMemory<Entity> Entities,
    long StructuralVersion)
{
    public int Length => Ids.Length;
    public bool IsEmpty => Ids.IsEmpty;
}
