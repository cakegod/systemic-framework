using System.Collections.Immutable;
using Systemic.Kernel;
using Systemic.Extensions;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Gameplay2D;

public sealed class Gameplay2DModule : ISystemicExtensionModule, ISystemicSystem
{
    private static readonly ImmutableArray<SystemAccess> s_access =
        [SystemAccess.ReadWorld, SystemAccess.DeferWorldWrites];

    public string Id => "systemic.gameplay2d";

    /// <summary>Gets the composition version recorded in every save that composes this module.</summary>
    public int Version => 1;

    public int Order => 20;

    /// <summary>Declares reads and deferred writes so the module can share a scheduling wave.</summary>
    public IReadOnlyList<SystemAccess> Access => s_access;

    public void Step(SystemicSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var world = context.World;
        var commands = context.Commands;
        // WHY: the tag index bounds this loop to live projectiles; scanning every entity made the
        // module cost O(all entities) per tick regardless of how many projectiles existed. Writes
        // are deferred so the module shares a wave instead of excluding every other system.
        foreach (var entityId in world.EntitiesWithTag(Gameplay2D.ProjectileTag).Span)
        {
            if (!world.HasTag(entityId, Gameplay2D.ProjectileTag)
                || !Gameplay2D.TryReadProjectile(entityId, world, out var projectile))
            {
                if (!world.HasTag(entityId, Gameplay2D.ProjectileTag))
                {
                    continue;
                }

                throw new InvalidOperationException(
                    $"Projectile {entityId} is missing a finite gameplay2d motion property.");
            }

            if (projectile.RemainingTicks <= 1)
            {
                commands.Remove(entityId);
                commands.RemoveSpatialBounds(entityId);
                context.DeferFact(new SimulationFact("gameplay2d.projectile.expired", entityId, 0));
                continue;
            }

            var x = projectile.X + projectile.VelocityX;
            var y = projectile.Y + projectile.VelocityY;
            if (!double.IsFinite(x) || !double.IsFinite(y)
                || Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue)
            {
                throw new InvalidOperationException($"Projectile {entityId} produced invalid motion state.");
            }

            var bounds = new Bounds2((float)x, (float)y,
                (float)projectile.HalfWidth, (float)projectile.HalfHeight);
            // Validate before queueing so an unindexable position fails here rather than at the barrier.
            world.ValidateSpatialBounds(bounds);
            commands.SetProperty(entityId, Gameplay2D.PositionX, x);
            commands.SetProperty(entityId, Gameplay2D.PositionY, y);
            commands.SetProperty(entityId, Gameplay2D.RemainingTicks, projectile.RemainingTicks - 1);
            commands.SetSpatialBounds(entityId, bounds);
        }
    }
}

/// <summary>Loads the optional Gameplay2D definitions declared by a game project.</summary>
public sealed class Gameplay2DContentModule : Systemic.Content.ISystemicContentModule
{
    public const string ModuleId = "systemic.gameplay2d";
    private const string Gameplay2DSchemaId = "https://press-the-button.dev/systemic/gameplay2d.schema.json";
    private static readonly string s_gameplay2DSchema = ReadSchema();

    public string Id => ModuleId;

    public int Version => 1;

    public Systemic.Content.ISystemicModuleContent Compile(
        Systemic.Content.ContentModuleContext context,
        System.Text.Json.JsonElement declaration)
    {
        var json = context.ReadJson(declaration, "gameplay2d", "$",
            Gameplay2DSchemaId, s_gameplay2DSchema, out var path);
        return Gameplay2DContentLoader.Parse(json.GetRawText(), path);
    }

    private static string ReadSchema()
    {
        using var stream = typeof(Gameplay2DContentModule).Assembly.GetManifestResourceStream(
            "Systemic.Gameplay2D.Schemas.gameplay2d.schema.json")
            ?? throw new InvalidOperationException("Embedded Gameplay2D schema was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Configure(
        Systemic.Host.SystemicGameBuilder builder,
        Systemic.Content.ISystemicModuleContent content)
    {
        builder.AddService((Gameplay2DContent)content);
        builder.AddExtension(new Gameplay2DModule());
    }
}
