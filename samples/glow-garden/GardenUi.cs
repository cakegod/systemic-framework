using Gum.Forms.Controls;
using Gum.DataTypes;
using Gum.Forms.DefaultVisuals.V3;
using Microsoft.Xna.Framework;
using MonoGameGum;

namespace GlowGarden;

/// <summary>Code-built Gum Forms; callbacks resolve the current session after every restart.</summary>
public sealed class GardenUi
{
    private readonly GardenGame _game;
    private readonly Label _stats, _flow, _slots, _creatureTree, _growthTree;
    private readonly Button[] _upgrades = new Button[5], _talents = new Button[6];
    private readonly Button _burstling, _gatherer, _bulk, _pause, _speed;
    private static readonly string[] UpgradeNames = ["Work rate", "Gatherer speed", "Carry capacity", "Pod growth", "Pod yield"];
    private static readonly string[] TalentNames = ["Echo", "Seedstorm", "Magnetism", "Chain Reaction", "Living Projectile", "Clone"];
    private static readonly string[] Descriptions = ["Delayed nearby pod impact", "Seeds establish new plants", "Pull Glow toward Gatherers", "Burst neighboring ripe pods", "Bounce to another ripe pod", "Combos create temporary workers"];

    public GardenUi(GardenGame game)
    {
        _game = game;
        GumService.Default.Root.Children.Clear();
        Label("GLOW GARDEN   /   a little colony of light", 20, 10, 800);
        _stats = Label("", 20, 37, 1230);
        _burstling = Button("", 20, 72, 222, () => Model.Hire(CreatureKind.Burstling));
        _gatherer = Button("", 250, 72, 222, () => Model.Hire(CreatureKind.Gatherer));
        _bulk = Button("", 487, 72, 170, () => game.Bulk = !game.Bulk);
        _pause = Button("", 670, 72, 175, game.TogglePause);
        _speed = Button("", 858, 72, 175, game.CycleSpeed);
        Button("[R] Restart", 1047, 72, 213, game.Restart);
        Label("THE GARDEN", 28, 125, 780);
        _flow = Label("", 28, 665, 810, 80);
        Label("Pods grow automatically. Burstlings scatter Glow.\nGatherers carry it home before you can spend it.\n[Space] pause   [Tab/Enter] navigate buttons   [Esc] quit", 28, 733, 810, 64);
        _creatureTree = Label("", 858, 120, 390);
        Button("[C] Refund creatures", 858, 146, 398, () => Model.ResetTree(UpgradeTree.Creatures));
        for (var i = 0; i < 3; i++)
        {
            var kind = (UpgradeKind)i;
            _upgrades[i] = Button("", 858, 185 + i * 36, 398, () => Model.Buy(kind, game.Bulk));
        }
        _growthTree = Label("", 858, 300, 398);
        Button("[M] Refund growth", 858, 326, 398, () => Model.ResetTree(UpgradeTree.MagicGrowth));
        for (var i = 3; i < 5; i++)
        {
            var kind = (UpgradeKind)i;
            _upgrades[i] = Button("", 858, 365 + (i - 3) * 36, 398, () => Model.Buy(kind, game.Bulk));
        }
        _slots = Label("", 858, 449, 398);
        for (var i = 0; i < 6; i++)
        {
            var talent = (Talent)i;
            _talents[i] = Button("", 858, 478 + i * 51, 398, () => Model.ToggleTalent(talent), 47);
        }
        Refresh();
    }

    private GardenSimulation Model => _game.Host.Simulation;
    private static Label Label(string text, int x, int y, int width, int height = 28)
    {
        var label = new Label { Text = text, X = x, Y = y, Width = width, Height = height };
        label.Visual.WidthUnits = DimensionUnitType.Absolute;
        label.Visual.HeightUnits = DimensionUnitType.Absolute;
        label.AddToRoot();
        return label;
    }
    private static Button Button(string text, int x, int y, int width, Action action, int height = 31)
    {
        var button = new Button { Text = text, X = x, Y = y, Width = width, Height = height };
        var visual = (ButtonVisual)button.Visual;
        visual.WidthUnits = DimensionUnitType.Absolute;
        visual.HeightUnits = DimensionUnitType.Absolute;
        visual.TextInstance.FontSize = 16;
        visual.BackgroundColor = new Color(29, 60, 62);
        visual.ForegroundColor = new Color(218, 246, 226);
        visual.FocusedIndicatorColor = new Color(248, 210, 111);
        button.Click += (_, _) => action();
        button.AddToRoot();
        return button;
    }
    public void Refresh()
    {
        var m = Model;
        var elapsed = TimeSpan.FromSeconds(m.Tick / (double)_game.Host.Game.Clock.TickRateHz);
        _stats.Text = $"SPENDABLE {m.Glow} Glow   |   BANKED (lifetime) {m.BankedGlow}   |   Plants {m.Plants.Count}/64   Burstlings {m.Count(CreatureKind.Burstling)}   Gatherers {m.Count(CreatureKind.Gatherer)}   Time {elapsed:mm\\:ss}";
        _burstling.Text = $"[B] Hire Burstling {m.HirePrice(CreatureKind.Burstling)}G";
        _gatherer.Text = $"[G] Hire Gatherer {m.HirePrice(CreatureKind.Gatherer)}G";
        _bulk.Text = $"[X] Buy {(_game.Bulk ? "MAX" : "ONE")}";
        _pause.Text = _game.Paused ? "[Space] Resume" : "[Space] Pause";
        _speed.Text = $"[F] Speed {_game.Speed}x";
        _creatureTree.Text = $"CREATURES  /  refundable {m.Spent(UpgradeTree.Creatures)}G";
        _growthTree.Text = $"MAGIC & GROWTH  /  refundable {m.Spent(UpgradeTree.MagicGrowth)}G";
        for (var i = 0; i < 5; i++)
        {
            var kind = (UpgradeKind)i;
            _upgrades[i].Text = $"[{i + 1}] {UpgradeNames[i]}  Lv{m.Rank(kind)}  {(m.Rank(kind) == 25 ? "MAX" : $"{m.Price(kind)}G")}";
        }
        _slots.Text = $"TALENTS   {m.ActiveTalentCount}/3 slots   /   [Q W E A S D]";
        for (var i = 0; i < 6; i++)
        {
            var active = m.IsTalentActive((Talent)i);
            _talents[i].Text = $"{(active ? "ON" : "off")}  {TalentNames[i]}\n{Descriptions[i]}";
            ((ButtonVisual)_talents[i].Visual).BackgroundColor = active ? new Color(61, 98, 69) : new Color(29, 60, 62);
        }
        _flow.Text = $"PRODUCING: {m.BurstCount} pod bursts  |  loose Glow {m.Motes.Sum(x => x.Value)}\nCOLLECTING: {m.Creatures.Sum(x => x.CarriedGlow)} carried  |  RETURNED: {m.BankedGlow} Glow\nGrowth {1 + m.Rank(UpgradeKind.PodGrowth) * .12f:0.00}x  |  Gatherer speed {1 + m.Rank(UpgradeKind.GathererSpeed) * .1f:0.00}x  |  Load {3 + m.Rank(UpgradeKind.GathererCapacity)} Glow";
    }
}
