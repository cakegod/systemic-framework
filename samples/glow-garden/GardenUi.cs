using Gum.Forms.Controls;
using Gum.DataTypes;
using Gum.Forms.DefaultVisuals.V3;
using Microsoft.Xna.Framework;
using MonoGameGum;
using Systemic.Host;
using Systemic.Presentation;

namespace GlowGarden;

/// <summary>Code-built Gum Forms; callbacks resolve the current session after every restart.</summary>
public sealed class GardenUi
{
    private readonly GardenGame _game;
    private readonly GardenTickDiagnostics _tickDiagnostics = new();
    private readonly Func<IReadOnlyList<SystemTickStats>> _sampleStats;
    private readonly string _schedulerWaveSummary;
    private readonly int _schedulerWorkerCount;
    private readonly Label _stats, _flow, _slots, _creatureTree, _growthTree;
    private readonly Label _cueDiagnostics, _debug;
    private readonly Button[] _upgrades = new Button[5], _talents = new Button[6];
    private readonly Button _burstling, _gatherer, _bulk, _pause, _speed;
    private static readonly string[] UpgradeNames = ["Work rate", "Gatherer speed", "Carry capacity", "Pod growth", "Pod yield"];
    private static readonly string[] TalentNames = ["Echo", "Seedstorm", "Magnetism", "Chain Reaction", "Living Projectile", "Clone"];
    private static readonly string[] Descriptions = ["Delayed nearby pod impact", "Seeds establish new plants", "Pull Glow toward Gatherers", "Burst neighboring ripe pods", "Bounce to another ripe pod", "Combos create temporary workers"];

    public GardenUi(GardenGame game)
    {
        _game = game;
        _sampleStats = SnapshotStats;
        _schedulerWaveSummary = GardenTickDiagnostics.FormatWaves(game.Host.Game.SchedulerWaves);
        _schedulerWorkerCount = game.Host.Game.SchedulerWorkerCount;
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
        _cueDiagnostics = Label("", 210, 125, 620);
        _debug = Label("", 28, 151, 795, 92);
        _flow = Label("", 28, 665, 810, 84);
        _saveStatus = Label("", 28, 754, 810, 44);
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
    private readonly Label _saveStatus;
    internal static string FormatCueDiagnostics(PresentationCueStream stream, long displayDropped, long expired = 0) =>
        $"Cues dropped {stream.DroppedCount} (rejected {stream.RejectedCount})  |  display dropped {displayDropped}  expired {expired}  |  [F3]";

    private IReadOnlyList<SystemTickStats> SnapshotStats() => _game.Host.Game.TickStats.Snapshot();

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
        var tickRate = _game.Host.Game.Clock.TickRateHz;
        var elapsed = TimeSpan.FromSeconds(m.Tick / (double)tickRate);
        var podsPerSecond = elapsed.TotalSeconds > 0 ? m.BurstCount / elapsed.TotalSeconds : 0;
        var bankedPerSecond = elapsed.TotalSeconds > 0 ? m.BankedGlow / elapsed.TotalSeconds : 0;
        _cueDiagnostics.Text = FormatCueDiagnostics(_game.Host.Game.PresentationCues(),
            _game.DisplayDroppedCount, _game.ExpiredCueCount);
        _debug.Text = _game.DebugOverlayEnabled
            ? $"Zoom {_game.Viewport.Zoom:0.00}x  |  drawn {_game.VisibleObjectCount}  culled {_game.CulledObjectCount}  |  active cues {_game.ActiveCueCount}  textures {_game.CachedCueTextureCount}\n{_tickDiagnostics.GetText(m.Tick, tickRate, _sampleStats)}\nWorkers {_schedulerWorkerCount}  |  {_schedulerWaveSummary}\nGrid: red=blocked green=scent  |  [PgUp/PgDn] zoom   [I J K L] pan   [Home] reset view"
            : string.Empty;
        _stats.Text = $"SPENDABLE {m.Glow} Glow   |   BANKED (lifetime) {m.BankedGlow}   |   Plants {m.Plants.Count}/64   Burstlings {m.Count(CreatureKind.Burstling)}   Gatherers {m.Count(CreatureKind.Gatherer)}   Time {elapsed:mm\\:ss}";
        _saveStatus.Text = $"Pods grow; Burstlings scatter Glow; Gatherers bank it at home.\n{_game.SaveStatus}   [Space] pause   [Esc] quit";
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
            var effect = kind switch
            {
                UpgradeKind.BurstlingWorkRate => $"Work rest {Math.Max(3, 30 - m.Rank(kind)) / (double)tickRate:0.00}s",
                UpgradeKind.PodYield => $"Yield {8 + 2 * m.Rank(kind)} Glow/pod",
                _ => UpgradeNames[i]
            };
            _upgrades[i].Text = $"[{i + 1}] {effect}  Lv{m.Rank(kind)}  {(m.Rank(kind) == 25 ? "MAX" : $"{m.Price(kind)}G")}";
        }
        _slots.Text = $"TALENTS   {m.ActiveTalentCount}/3 slots   /   [Q W E A S D]";
        for (var i = 0; i < 6; i++)
        {
            var active = m.IsTalentActive((Talent)i);
            _talents[i].Text = $"{(active ? "ON" : "off")}  {TalentNames[i]}\n{Descriptions[i]}  ·  {m.EffectCount((Talent)i)} uses";
            ((ButtonVisual)_talents[i].Visual).BackgroundColor = active ? new Color(61, 98, 69) : new Color(29, 60, 62);
        }
        _flow.Text = $"PRODUCTION: {m.BurstCount} pods total  |  Loose {m.Motes.Sum(x => x.Value)} Glow\nCOLLECTION: {m.Creatures.Sum(x => x.CarriedGlow)} carried  |  Returned {m.BankedGlow} Glow total\nAVERAGE since restart: {podsPerSecond:0.00} pods/s  |  {bankedPerSecond:0.00} banked Glow/s\nGrowth {1 + m.Rank(UpgradeKind.PodGrowth) * .12f:0.00}x  |  Gatherer speed {1 + m.Rank(UpgradeKind.GathererSpeed) * .1f:0.00}x  |  Load {3 + m.Rank(UpgradeKind.GathererCapacity)} Glow";
    }
}
