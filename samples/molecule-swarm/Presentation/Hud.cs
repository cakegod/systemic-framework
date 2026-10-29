using System.Globalization;
using System.Numerics;

namespace MoleculeSwarm;

/// <summary>
/// The overlay: the core's health bar, the kill and gold counters, the turret hotbar, and the paused
/// and game-over banners. It owns its own layout so the click handler and the drawing agree.
/// </summary>
internal sealed class Hud
{
    public const int SlotSize = 46;
    public const int Gap = 6;
    public const int Padding = 8;
    public const int SlotCount = 4;

    private static float PanelWidth => (SlotSize * SlotCount) + (Gap * (SlotCount - 1)) + (Padding * 2);

    private static float PanelLeft => (Sim.Width * 0.5f) - (PanelWidth * 0.5f);

    private static float PanelTop => Sim.Height - 62f;

    /// <summary>Which hotbar slot a click lands on, or -1 when it misses the panel.</summary>
    public static int SlotAt(Vector2 click)
    {
        var left = PanelLeft;
        var top = PanelTop + 8f;
        if (click.Y < top - 4f || click.Y > top + SlotSize + 4f)
        {
            return -1;
        }

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var slotLeft = left + Padding + (slot * (SlotSize + Gap));
            if (click.X >= slotLeft - 2f && click.X <= slotLeft + SlotSize + 2f)
            {
                return slot;
            }
        }

        return -1;
    }

    public void Draw(Sim sim, ShapeBatch batch, int selectedKind, bool paused, bool gameOver)
    {
        DrawHealthBar(sim, batch);

        var centerX = Sim.Width * 0.5f;
        DrawCounter(batch, new Vector2(centerX - 148f, 68f), DrawOrcIcon, sim.Kills);
        DrawCounter(batch, new Vector2(centerX - 8f, 68f), DrawGoldIcon, sim.Gold);
        DrawCounter(batch, new Vector2(centerX + 132f, 68f), DrawSkullIcon, sim.Leaks);

        DrawHotbar(sim, batch, selectedKind);
        DrawLoiter(sim, batch);

        batch.AddTextShadow(
            "1-3 PICK   CLICK GROUND TO BUILD   CLICK A TURRET TO UPGRADE   SPACE SEND WAVE   P PAUSE",
            14f, 12f, 1, new Vector4(0.94f, 0.94f, 0.88f, 0.55f));

        if (paused)
        {
            batch.AddQuad(0, 0, Sim.Width, Sim.Height, new Vector4(0f, 0f, 0f, 0.45f));
            batch.AddTextCentered("PAUSED", centerX, (Sim.Height * 0.5f) - 20f, 4, new Vector4(1f, 1f, 1f, 0.95f));
        }

        if (gameOver)
        {
            batch.AddQuad(0, 0, Sim.Width, Sim.Height, new Vector4(0.06f, 0f, 0f, 0.62f));
            batch.AddTextCentered("THE KEEP HAS FALLEN", centerX, (Sim.Height * 0.5f) - 46f, 4,
                new Vector4(1f, 0.34f, 0.28f, 1f));
            batch.AddTextCentered($"WAVE {sim.Wave}   KILLS {sim.Kills}   LEAKED {sim.Leaks}", centerX,
                (Sim.Height * 0.5f) + 16f, 2, new Vector4(0.96f, 0.92f, 0.86f, 1f));
            batch.AddTextCentered("PRESS R TO TRY AGAIN", centerX, (Sim.Height * 0.5f) + 56f, 2,
                new Vector4(0.88f, 0.92f, 0.84f, 1f));
        }
    }

    private static void DrawHealthBar(Sim sim, ShapeBatch batch)
    {
        var centerX = Sim.Width * 0.5f;
        batch.AddTextShadow("HEALTH", centerX - 20f, 12f, 2, new Vector4(0.97f, 0.97f, 0.94f, 1f));

        const float Total = 320f;
        var left = centerX - (Total * 0.5f);
        const float Top = 34f;
        const float Height = 18f;
        var fraction = Math.Clamp(sim.CoreHp / (float)Sim.CoreHpMax, 0f, 1f);

        batch.AddQuad(left - 3f, Top - 3f, left + Total + 3f, Top + Height + 3f,
            new Vector4(0f, 0f, 0f, 0.55f));
        batch.AddQuad(left, Top, left + Total, Top + Height, new Vector4(0.15f, 0.05f, 0.05f, 1f));
        batch.AddQuad(left, Top, left + (Total * fraction), Top + Height, new Vector4(0.84f, 0.10f, 0.08f, 1f));
        batch.AddQuad(left, Top, left + (Total * fraction), Top + 6f, new Vector4(0.97f, 0.30f, 0.22f, 0.55f));
        batch.AddQuad(left, Top, left + (Total * fraction), Top + 2f, new Vector4(1f, 0.52f, 0.42f, 0.55f));

        batch.AddTextCentered($"{Math.Max(0, sim.CoreHp)} / {Sim.CoreHpMax}", centerX, Top + 5f, 2,
            new Vector4(1f, 1f, 1f, 0.96f));
    }

    private static void DrawCounter(ShapeBatch batch, Vector2 anchor, Action<ShapeBatch, Vector2> icon, int value)
    {
        icon(batch, anchor + new Vector2(7f, 6f));
        batch.AddTextShadow(ShapeBatch.Compact(value), anchor.X + 18f, anchor.Y - 1f, 2,
            new Vector4(0.97f, 0.97f, 0.92f, 1f));
    }

    private static void DrawOrcIcon(ShapeBatch batch, Vector2 center)
    {
        batch.AddCircle(center, 8f, 12, new Vector4(0.16f, 0.24f, 0.10f, 1f));
        batch.AddCircle(center, 6.6f, 12, new Vector4(0.40f, 0.62f, 0.25f, 1f));
        batch.AddQuad(center.X - 6.2f, center.Y - 7.4f, center.X + 6.2f, center.Y - 2.6f,
            new Vector4(0.35f, 0.29f, 0.15f, 1f));
        batch.AddQuad(center.X - 4.2f, center.Y - 1.4f, center.X - 1.2f, center.Y + 1.6f,
            new Vector4(0.06f, 0.06f, 0.04f, 1f));
        batch.AddQuad(center.X + 1.2f, center.Y - 1.4f, center.X + 4.2f, center.Y + 1.6f,
            new Vector4(0.06f, 0.06f, 0.04f, 1f));
    }

    private static void DrawGoldIcon(ShapeBatch batch, Vector2 center)
    {
        batch.AddQuad(center.X - 7.5f, center.Y + 0.5f, center.X + 7.5f, center.Y + 6.5f,
            new Vector4(0.78f, 0.55f, 0.14f, 1f));
        batch.AddQuad(center.X - 6f, center.Y - 4.5f, center.X + 6f, center.Y + 0.5f,
            new Vector4(1f, 0.82f, 0.30f, 1f));
        batch.AddQuad(center.X - 3.4f, center.Y - 2.8f, center.X + 1.2f, center.Y - 0.4f,
            new Vector4(1f, 0.94f, 0.62f, 0.9f));
    }

    private static void DrawSkullIcon(ShapeBatch batch, Vector2 center)
    {
        var bone = new Vector4(0.92f, 0.91f, 0.86f, 1f);
        batch.AddQuad(center.X - 4.6f, center.Y + 2.4f, center.X + 4.6f, center.Y + 6.4f, bone);
        batch.AddCircle(center + new Vector2(0f, -1.4f), 6.4f, 12, bone);
        batch.AddQuad(center.X - 3.6f, center.Y - 3.2f, center.X - 1.1f, center.Y + 0.4f,
            new Vector4(0.10f, 0.09f, 0.08f, 1f));
        batch.AddQuad(center.X + 1.1f, center.Y - 3.2f, center.X + 3.6f, center.Y + 0.4f,
            new Vector4(0.10f, 0.09f, 0.08f, 1f));
    }

    private static void DrawHotbar(Sim sim, ShapeBatch batch, int selectedKind)
    {
        var panelLeft = PanelLeft;
        var panelTop = PanelTop;
        var slotTop = panelTop + 8f;

        batch.AddQuad(panelLeft - 2f, panelTop - 2f, panelLeft + PanelWidth + 2f, panelTop + 56f,
            new Vector4(0f, 0f, 0f, 0.45f));
        batch.AddQuad(panelLeft, panelTop, panelLeft + PanelWidth, panelTop + 52f,
            new Vector4(0.09f, 0.09f, 0.10f, 0.92f));
        batch.AddQuad(panelLeft, panelTop, panelLeft + PanelWidth, panelTop + 2f,
            new Vector4(0.20f, 0.20f, 0.22f, 1f));

        for (var slot = 0; slot < SlotCount; slot++)
        {
            var left = panelLeft + Padding + (slot * (SlotSize + Gap));
            var selected = slot == selectedKind;
            batch.AddQuad(left, slotTop, left + SlotSize, slotTop + SlotSize,
                selected ? new Vector4(0.27f, 0.25f, 0.16f, 0.96f) : new Vector4(0.15f, 0.15f, 0.16f, 0.9f));
            var border = selected ? new Vector4(0.94f, 0.86f, 0.48f, 1f) : new Vector4(0.30f, 0.30f, 0.33f, 1f);
            batch.AddQuad(left, slotTop, left + SlotSize, slotTop + 1.6f, border);
            batch.AddQuad(left, slotTop + SlotSize - 1.6f, left + SlotSize, slotTop + SlotSize, border);
            batch.AddQuad(left, slotTop, left + 1.6f, slotTop + SlotSize, border);
            batch.AddQuad(left + SlotSize - 1.6f, slotTop, left + SlotSize, slotTop + SlotSize, border);

            var number = (slot + 1).ToString(CultureInfo.InvariantCulture);
            batch.AddTextShadow(number, left + 4f, slotTop + 3f, 1, new Vector4(0.94f, 0.94f, 0.88f, 0.85f));

            var center = new Vector2(left + (SlotSize * 0.5f), slotTop + (SlotSize * 0.5f) + 3f);
            DrawSlotIcon(batch, slot, center);
        }

        var waveText = sim.WaveActive
            ? $"WAVE {Math.Max(1, sim.Wave)}  ·  {sim.PendingSpawns} INCOMING"
            : sim.Wave == 0
                ? "WAVE 1  ·  READY"
                : $"WAVE {Math.Max(1, sim.Wave)}  ·  NEXT IN {MathF.Max(0f, sim.Intermission):0}S";
        batch.AddTextShadow(waveText, panelLeft, panelTop - 18f, 1, new Vector4(0.96f, 0.95f, 0.90f, 0.7f));
    }

    private static void DrawSlotIcon(ShapeBatch batch, int slot, Vector2 center)
    {
        switch (slot)
        {
            case 0:
                batch.AddCircle(center, 8f, 10, new Vector4(0.20f, 0.21f, 0.23f, 1f));
                batch.AddCircle(center, 5.6f, 10, new Vector4(0.60f, 0.62f, 0.66f, 1f));
                batch.AddThickLine(center + new Vector2(-1f, 0f), center + new Vector2(9f, -7f), 2.6f,
                    new Vector4(0.88f, 0.89f, 0.92f, 1f));
                break;
            case 1:
                batch.AddCircle(center, 8f, 10, new Vector4(0.24f, 0.21f, 0.18f, 1f));
                batch.AddCircle(center + new Vector2(0f, 1f), 5.4f, 10, new Vector4(0.52f, 0.48f, 0.42f, 1f));
                batch.AddCircle(center + new Vector2(0f, 2f), 2.6f, 8, new Vector4(1f, 0.60f, 0.18f, 1f));
                batch.AddQuad(center.X - 1.2f, center.Y - 9f, center.X + 1.2f, center.Y - 3f,
                    new Vector4(0.74f, 0.70f, 0.64f, 1f));
                break;
            case 2:
                batch.AddCircle(center, 8f, 12, new Vector4(0.10f, 0.18f, 0.20f, 1f));
                batch.AddCircle(center, 4.4f, 12, new Vector4(0.48f, 0.93f, 0.96f, 1f));
                batch.AddThickLine(center + new Vector2(-9f, 0f), center + new Vector2(-5f, 0f), 1.8f,
                    new Vector4(0.80f, 1f, 1f, 0.9f));
                batch.AddThickLine(center + new Vector2(5f, 0f), center + new Vector2(9f, 0f), 1.8f,
                    new Vector4(0.80f, 1f, 1f, 0.9f));
                break;
            default:
                batch.AddThickLine(center + new Vector2(-7f, 6f), center + new Vector2(4f, -5f), 3.4f,
                    new Vector4(0.86f, 0.70f, 0.28f, 1f));
                batch.AddCircle(center + new Vector2(5.5f, -6f), 4.2f, 10, new Vector4(1f, 0.90f, 0.48f, 1f));
                break;
        }
    }

    private static void DrawLoiter(Sim sim, ShapeBatch batch)
    {
        const float Left = 16f;
        var top = Sim.Height - 54f;
        batch.AddQuad(Left - 2f, top - 2f, Left + 42f, top + 42f, new Vector4(0f, 0f, 0f, 0.35f));
        batch.AddQuad(Left, top, Left + 40f, top + 40f, new Vector4(0.10f, 0.10f, 0.11f, 0.92f));
        batch.AddQuad(Left, top, Left + 40f, top + 2f, new Vector4(0.22f, 0.22f, 0.24f, 1f));
        batch.AddTextCentered(ShapeBatch.Compact(sim.Leaks), Left + 20f, top + 13f, 2,
            new Vector4(0.96f, 0.94f, 0.88f, 1f));
        batch.AddTextCentered("LOST", Left + 20f, top + 30f, 1, new Vector4(0.72f, 0.70f, 0.64f, 0.8f));
    }
}
