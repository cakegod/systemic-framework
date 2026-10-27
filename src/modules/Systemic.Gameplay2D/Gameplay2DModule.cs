using Systemic.Kernel;
using Systemic.Extensions;
using Systemic.Host;
using Systemic.Rules;

namespace Systemic.Gameplay2D;

public sealed class Gameplay2DModule : ISystemicExtensionModule, ISystemicSystem
{
    public string Id => "systemic.gameplay2d";

    public int Order => 20;

    public void Register(EngineBuilder engine) => ArgumentNullException.ThrowIfNull(engine);

    public void Step(SystemicSystemContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var world = context.GetWritableWorld();
        using var transaction = world.BeginTransaction();
        foreach (var entity in world.Entities)
        {
            if (entity is not { ToRemove: false } || !world.HasTag(entity, Gameplay2D.ProjectileTag))
            {
                continue;
            }

            if (!Gameplay2D.TryReadProjectile(entity, world, out var projectile))
            {
                throw new InvalidOperationException($"Projectile {entity.Id} is missing a finite gameplay2d motion property.");
            }

            if (projectile.RemainingTicks <= 1)
            {
                entity.ToRemove = true;
                _ = world.RemoveSpatialBounds(entity);
                context.Publish(new SimulationFact("gameplay2d.projectile.expired", entity.Id, 0));
                continue;
            }

            var x = projectile.X + projectile.VelocityX;
            var y = projectile.Y + projectile.VelocityY;
            if (!double.IsFinite(x) || !double.IsFinite(y)
                || Math.Abs(x) > float.MaxValue || Math.Abs(y) > float.MaxValue
                || !entity.TrySetProperty(world, Gameplay2D.PositionX, x)
                || !entity.TrySetProperty(world, Gameplay2D.PositionY, y)
                || !entity.TrySetProperty(world, Gameplay2D.RemainingTicks, projectile.RemainingTicks - 1))
            {
                throw new InvalidOperationException($"Projectile {entity.Id} produced invalid motion state.");
            }

            world.SetSpatialBounds(entity, new Bounds2((float)x, (float)y,
                (float)projectile.HalfWidth, (float)projectile.HalfHeight));
        }

        transaction.Commit();
    }
}

/// <summary>Loads the optional Gameplay2D definitions declared by a game project.</summary>
public sealed class Gameplay2DContentModule : Systemic.Content.ISystemicContentModule
{
    public const string ModuleId = "systemic.gameplay2d";
    private const string Gameplay2DSchemaId = "https://press-the-button.dev/systemic/gameplay2d.schema.json";
    private static readonly string s_gameplay2DSchema = ReadSchema();

    public string Id => ModuleId;

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
