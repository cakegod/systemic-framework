using System.Globalization;
using System.Numerics;

namespace MoleculeSwarm;

/// <summary>
/// Everything drawn in world space with the shared shape batch: the core, the emplacements, the
/// shots in the air, the build preview and the cursor.
/// </summary>
internal sealed class WorldPass
{
    private static readonly Vector4 BeamCore = new(0.98f, 0.95f, 1f, 0.95f);

    private static readonly (float X, float Y)[] Corners = [(-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f)];

    public void Draw(Sim sim, ShapeBatch batch, int hoveredMount, int selectedKind, Vector2 mouse)
    {
        DrawCore(sim, batch);

        for (var index = 0; index < sim.Turrets.Count; index++)
        {
            DrawTurret(batch, sim.Turrets.TurretAt(index));
        }

        for (var index = 0; index < sim.Effects.TracerCount; index++)
        {
            var tracer = sim.Effects.TracerAt(index);
            var alpha = Math.Clamp(tracer.Life / tracer.MaxLife, 0f, 1f);
            AddBeam(batch, tracer.From, tracer.To, tracer.Width, alpha);
        }

        for (var index = 0; index < sim.Turrets.ShellCount; index++)
        {
            var shell = sim.Turrets.ShellAt(index);
            var direction = shell.Target - shell.Position;
            if (direction.LengthSquared() < 0.01f)
            {
                direction = Vector2.UnitY;
            }
            else
            {
                direction = Vector2.Normalize(direction);
            }

            for (var step = 1; step <= 6; step++)
            {
                var dot = shell.Position - (direction * step * 7f);
                batch.AddCircle(dot, 3.2f - (step * 0.32f), 8,
                    new Vector4(0.78f, 0.62f, 1f, 0.75f - (step * 0.1f)));
            }

            batch.AddCircle(shell.Position, 4.2f, 10, new Vector4(0.92f, 0.84f, 1f, 1f));
            batch.AddCircle(shell.Position, 2.2f, 8, new Vector4(1f, 1f, 1f, 1f));
        }

        for (var index = 0; index < sim.Effects.BoomCount; index++)
        {
            var boom = sim.Effects.BoomAt(index);
            var life = Math.Clamp(boom.Life / boom.MaxLife, 0f, 1f);
            var radius = boom.Radius * (1.1f - (0.55f * life));
            batch.AddCircle(boom.Center, radius * 0.62f, 20, new Vector4(1f, 0.70f, 0.90f, 0.30f * life));
            batch.AddCircle(boom.Center, radius * 0.34f, 14, new Vector4(1f, 0.96f, 1f, 0.75f * life));
            batch.AddRing(boom.Center, radius, 3f, 26, new Vector4(0.86f, 0.66f, 1f, 0.45f * life));
        }

        DrawHover(sim, batch, hoveredMount, selectedKind);
        DrawCursor(batch, mouse);
    }

    private static void DrawCore(Sim sim, ShapeBatch batch)
    {
        var center = Terrain.CoreCenter;
        const float Half = 26f;

        batch.AddQuad(center.X - Half - 4f, center.Y - Half + 8f, center.X + Half + 4f, center.Y + Half + 8f,
            new Vector4(0f, 0f, 0f, 0.30f));
        batch.AddQuad(center.X - Half, center.Y - Half, center.X + Half, center.Y + Half,
            new Vector4(0.10f, 0.08f, 0.17f, 1f));
        batch.AddQuad(center.X - Half + 3f, center.Y - Half + 3f, center.X + Half - 3f, center.Y + Half - 3f,
            new Vector4(0.32f, 0.26f, 0.46f, 1f));
        batch.AddQuad(center.X - 12f, center.Y - 12f, center.X + 12f, center.Y + 12f,
            new Vector4(0.06f, 0.05f, 0.10f, 1f));

        foreach (var corner in Corners)
        {
            var point = new Vector2(
                center.X + (corner.X * Half),
                center.Y + (corner.Y * Half));
            batch.AddCircle(point, 8.5f, 12, new Vector4(0.09f, 0.07f, 0.15f, 1f));
            batch.AddCircle(point, 7f, 12, new Vector4(0.52f, 0.46f, 0.72f, 1f));
            batch.AddCircle(point, 3.2f, 8, new Vector4(0.85f, 0.72f, 0.98f, 1f));
        }

        batch.AddQuad(center.X - 1.6f, center.Y - 30f, center.X + 1.6f, center.Y - 2f,
            new Vector4(0.12f, 0.10f, 0.20f, 1f));
        batch.AddQuad(center.X + 1.6f, center.Y - 30f, center.X + 14f, center.Y - 21f,
            new Vector4(0.95f, 0.30f, 0.62f, 1f));

        var fraction = Math.Clamp(sim.CoreHp / (float)Sim.CoreHpMax, 0f, 1f);
        batch.AddArc(center, 40f, 8f, 0.035f, 46, -MathF.PI / 2f, MathF.Tau,
            new Vector4(0.06f, 0.05f, 0.11f, 0.70f));
        if (fraction > 0f)
        {
            var color = fraction > 0.5f
                ? new Vector4(0.55f, 0.95f, 0.92f, 0.95f)
                : fraction > 0.25f
                    ? new Vector4(0.98f, 0.85f, 0.50f, 0.95f)
                    : new Vector4(1f, 0.36f, 0.56f, 0.95f);
            batch.AddArc(center, 40f, 7f, 0.035f, 46, -MathF.PI / 2f, MathF.Tau * fraction, color);
        }
    }

    private static void DrawTurret(ShapeBatch batch, Turret turret)
    {
        var center = turret.Position;
        var direction = new Vector2(MathF.Cos(turret.Aim), MathF.Sin(turret.Aim));

        batch.AddCircle(center + new Vector2(0f, 3f), 15f, 16, new Vector4(0f, 0f, 0f, 0.40f));
        batch.AddCircle(center, 14f, 18, new Vector4(0.09f, 0.07f, 0.15f, 1f));
        batch.AddCircle(center, 12f, 18, new Vector4(0.30f, 0.24f, 0.44f, 1f));
        batch.AddCircle(center, 10f, 16, new Vector4(0.07f, 0.05f, 0.11f, 1f));
        batch.AddCircle(center, 8.6f, 16, new Vector4(0.52f, 0.24f, 0.46f, 1f));

        for (var index = 0; index < 4; index++)
        {
            var angle = (MathF.Tau * index / 4f) + 0.78f;
            var stud = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 6.8f;
            batch.AddCircle(stud, 1.6f, 6, new Vector4(0.82f, 0.70f, 0.98f, 1f));
        }

        var body = turret.Kind switch
        {
            TurretKind.Gun => new Vector4(0.72f, 0.78f, 0.96f, 1f),
            TurretKind.Mortar => new Vector4(0.95f, 0.82f, 0.55f, 1f),
            _ => new Vector4(0.55f, 0.95f, 0.98f, 1f),
        };

        // A rectangular housing pointing down the barrel reads as a gun emplacement from above.
        batch.AddBar(center, direction, 10f, 7f, 6.4f, new Vector4(0.05f, 0.04f, 0.09f, 1f));
        batch.AddBar(center, direction, 8.6f, 5.6f, 5f, body);
        batch.AddCircle(center - (direction * 1.5f), 3.4f, 10, new Vector4(0.88f, 0.82f, 1f, 1f));
        batch.AddThickLine(center + (direction * 8f), center + (direction * (17f + turret.Level)),
            2.4f + (turret.Level * 0.26f), new Vector4(0.92f, 0.90f, 1f, 1f));
        batch.AddCircle(center + (direction * (17f + turret.Level)), 2.4f, 8,
            new Vector4(1f, 0.72f, 0.86f, 0.35f + (0.65f * turret.Flash)));

        if (turret.Level > 1)
        {
            batch.AddRing(center, 10.6f, 1.1f, 16, new Vector4(1f, 0.80f, 0.92f, 0.9f));
        }
    }

    private static void AddBeam(ShapeBatch batch, Vector2 from, Vector2 to, float width, float alpha)
    {
        if (alpha <= 0.01f)
        {
            return;
        }

        batch.AddThickLine(from, to, width * 5f, new Vector4(0.72f, 0.52f, 1f, alpha * 0.12f));
        batch.AddThickLine(from, to, width * 2.4f, new Vector4(0.95f, 0.80f, 1f, alpha * 0.24f));
        batch.AddThickLine(from, to, width,
            new Vector4(BeamCore.X, BeamCore.Y, BeamCore.Z, alpha * BeamCore.W));
    }

    private static void DrawHover(Sim sim, ShapeBatch batch, int hoveredMount, int selectedKind)
    {
        if (hoveredMount < 0)
        {
            return;
        }

        var center = TurretStats.MountCenter(hoveredMount);
        var index = sim.TurretIndexAtMount(hoveredMount);
        if (index >= 0)
        {
            var turret = sim.Turrets.TurretAt(index);
            batch.AddRing(center, TurretStats.RangeAtLevel(turret.Kind, turret.Level), 1.4f, 44,
                new Vector4(0.82f, 0.70f, 1f, 0.30f));
            batch.AddRing(center, 12f, 1.2f, 18, new Vector4(0.86f, 0.74f, 1f, 0.85f));
            if (turret.Level < TurretStats.MaxLevel)
            {
                var cost = TurretStats.UpgradeCost(turret.Kind, turret.Level);
                batch.AddTextCentered($"UP {cost}", center.X, center.Y + 17f, 1,
                    sim.Gold >= cost ? new Vector4(0.92f, 0.84f, 1f, 1f) : new Vector4(1f, 0.42f, 0.60f, 1f));
            }
            else
            {
                batch.AddTextCentered("MAX", center.X, center.Y + 17f, 1, new Vector4(0.80f, 0.76f, 0.95f, 1f));
            }

            return;
        }

        if (!sim.IsBuildable(hoveredMount))
        {
            return;
        }

        var kind = (TurretKind)selectedKind;
        batch.AddRing(center, TurretStats.Range(kind), 1.2f, 44, new Vector4(0.78f, 0.72f, 1f, 0.20f));
        const float Half = 15f;
        const float Arm = 6f;
        var ink = new Vector4(0.92f, 0.90f, 1f, 0.75f);
        foreach (var corner in Corners)
        {
            var cornerX = center.X + (corner.X * Half);
            var cornerY = center.Y + (corner.Y * Half);
            batch.AddQuad(MathF.Min(cornerX, cornerX - (corner.X * Arm)), cornerY - 1f,
                MathF.Max(cornerX, cornerX - (corner.X * Arm)), cornerY + 1f, ink);
            batch.AddQuad(cornerX - 1f, MathF.Min(cornerY, cornerY - (corner.Y * Arm)),
                cornerX + 1f, MathF.Max(cornerY, cornerY - (corner.Y * Arm)), ink);
        }

        var price = TurretStats.Cost(kind);
        batch.AddTextCentered(price.ToString(CultureInfo.InvariantCulture), center.X, center.Y + 19f, 1,
            sim.Gold >= price ? new Vector4(0.92f, 0.84f, 1f, 1f) : new Vector4(1f, 0.42f, 0.60f, 1f));
    }

    private static void DrawCursor(ShapeBatch batch, Vector2 mouse)
    {
        if (mouse.X < 0f || mouse.Y < 0f || mouse.X > Sim.Width || mouse.Y > Sim.Height)
        {
            return;
        }

        var white = new Vector4(1f, 1f, 1f, 0.92f);
        var dark = new Vector4(0.04f, 0.03f, 0.07f, 0.92f);
        batch.AddQuad(mouse.X, mouse.Y, mouse.X + 2f, mouse.Y + 17f, dark);
        batch.AddThickLine(mouse + new Vector2(1f, 1f), mouse + new Vector2(1f, 14f), 1.4f, white);
        batch.AddThickLine(mouse + new Vector2(1f, 1f), mouse + new Vector2(11f, 9f), 1.4f, white);
        batch.AddQuad(mouse.X + 5f, mouse.Y + 9f, mouse.X + 11f, mouse.Y + 11f, white);
    }
}
