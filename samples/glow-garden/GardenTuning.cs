using Systemic.Content;

namespace GlowGarden;

/// <summary>Validated garden settings resolved once from the content pack.</summary>
public sealed record GardenTuning(
    int TalentSlots,
    float ScoutRadius,
    int Patience,
    float TrailDecay,
    float TrailThreshold,
    float TrailStrength,
    int EchoDelay,
    int EchoExpiry,
    float EchoRadius,
    int SeedstormDelay,
    int SeedstormExpiry,
    float SeedstormOffset,
    float MagnetismRadius,
    float MagnetismPull,
    float ChainRadius,
    int ChainBursts,
    float ProjectileRadius,
    int CloneLifetime)
{
    private const string Slots = "garden.talents.slots";
    public static GardenTuning Default { get; } = new(3, 260, 120, .995f, .4f, 10,
        12, 60, 180, 18, 60, 50, 90, 4, 100, 16, 240, 180);

    public static ContentTuningSchema Schema { get; } = new(
    [
        TuningProperty.Integer(Slots, minimum: 1, maximum: 6, defaultValue: Default.TalentSlots),
        TuningProperty.Number("garden.worker.scoutRadius", 8, GardenSimulation.Width, Default.ScoutRadius),
        TuningProperty.Integer("garden.worker.patience", 0, 3600, Default.Patience),
        TuningProperty.Number("garden.trail.decay", .5, .9999, Default.TrailDecay),
        TuningProperty.Number("garden.trail.threshold", .01, 100, Default.TrailThreshold),
        TuningProperty.Number("garden.trail.strength", 0, 100, Default.TrailStrength),
        TuningProperty.Integer("garden.echo.delay", 1, 600, Default.EchoDelay),
        TuningProperty.Integer("garden.echo.expiry", 1, 3600, Default.EchoExpiry),
        TuningProperty.Number("garden.echo.radius", 1, 960, Default.EchoRadius),
        TuningProperty.Integer("garden.seedstorm.delay", 1, 600, Default.SeedstormDelay),
        TuningProperty.Integer("garden.seedstorm.expiry", 1, 3600, Default.SeedstormExpiry),
        TuningProperty.Number("garden.seedstorm.offset", 0, 400, Default.SeedstormOffset),
        TuningProperty.Number("garden.magnetism.radius", 1, 960, Default.MagnetismRadius),
        TuningProperty.Number("garden.magnetism.pull", 1, 32, Default.MagnetismPull),
        TuningProperty.Number("garden.chain.radius", 1, 960, Default.ChainRadius),
        TuningProperty.Integer("garden.chain.bursts", 1, 64, Default.ChainBursts),
        TuningProperty.Number("garden.projectile.radius", 1, 960, Default.ProjectileRadius),
        TuningProperty.Integer("garden.clone.lifetime", 1, 3600, Default.CloneLifetime),
        TuningProperty.Number("garden.talent.echo.uses", 0, defaultValue: 0),
        TuningProperty.Number("garden.talent.seedstorm.uses", 0, defaultValue: 0),
        TuningProperty.Number("garden.talent.magnetism.uses", 0, defaultValue: 0),
        TuningProperty.Number("garden.talent.chain.uses", 0, defaultValue: 0),
        TuningProperty.Number("garden.talent.projectile.uses", 0, defaultValue: 0),
        TuningProperty.Number("garden.talent.clone.uses", 0, defaultValue: 0),
    ], requiredTags: ["garden"]);

    public static GardenTuning From(TuningDocument document) => new(
        checked((int)document.GetInteger(Slots)),
        (float)document.GetNumber("garden.worker.scoutRadius"),
        checked((int)document.GetInteger("garden.worker.patience")),
        (float)document.GetNumber("garden.trail.decay"),
        (float)document.GetNumber("garden.trail.threshold"),
        (float)document.GetNumber("garden.trail.strength"),
        checked((int)document.GetInteger("garden.echo.delay")),
        checked((int)document.GetInteger("garden.echo.expiry")),
        (float)document.GetNumber("garden.echo.radius"),
        checked((int)document.GetInteger("garden.seedstorm.delay")),
        checked((int)document.GetInteger("garden.seedstorm.expiry")),
        (float)document.GetNumber("garden.seedstorm.offset"),
        (float)document.GetNumber("garden.magnetism.radius"),
        (float)document.GetNumber("garden.magnetism.pull"),
        (float)document.GetNumber("garden.chain.radius"),
        checked((int)document.GetInteger("garden.chain.bursts")),
        (float)document.GetNumber("garden.projectile.radius"),
        checked((int)document.GetInteger("garden.clone.lifetime")));
}
