namespace PixelMine;

internal sealed class MineHudFocus
{
    public const int UpgradeCount = 7;

    private readonly int[] _upgradeLevels = new int[UpgradeCount];
    private readonly float[] _upgradeFlashes = new float[UpgradeCount];
    private float _upgradeSeconds;
    private float _ballSeconds;
    private int _hoveredUpgrade = -1;

    public int UpgradeIndex { get; private set; } = -1;

    public float UpgradeReveal { get; private set; }

    public float BallReveal { get; private set; }

    public void InspectUpgrade(int index)
    {
        UpgradeIndex = index;
        _upgradeSeconds = 2f;
    }

    public void InspectBall() => _ballSeconds = 2f;

    public float UpgradeFlash(int index) => _upgradeFlashes[index];

    public void ObserveUpgrade(int index, int level)
    {
        if (level > _upgradeLevels[index])
        {
            _upgradeFlashes[index] = 1f;
        }

        _upgradeLevels[index] = level;
    }

    public void Update(TimeSpan elapsed, int hoveredUpgrade, bool inspectBall)
    {
        var seconds = Math.Max(0f, (float)elapsed.TotalSeconds);
        for (var index = 0; index < _upgradeFlashes.Length; index++)
        {
            _upgradeFlashes[index] = Math.Max(0f, _upgradeFlashes[index] - (seconds / 0.7f));
        }

        if (hoveredUpgrade >= 0 && (hoveredUpgrade != _hoveredUpgrade || UpgradeIndex == hoveredUpgrade))
        {
            InspectUpgrade(hoveredUpgrade);
        }
        else
        {
            _upgradeSeconds = Math.Max(0f, _upgradeSeconds - seconds);
        }

        _hoveredUpgrade = hoveredUpgrade;
        if (inspectBall)
        {
            InspectBall();
        }
        else
        {
            _ballSeconds = Math.Max(0f, _ballSeconds - seconds);
        }

        UpgradeReveal = Math.Clamp(UpgradeReveal + (seconds * (_upgradeSeconds > 0f ? 8f : -12f)), 0f, 1f);
        BallReveal = Math.Clamp(BallReveal + (seconds * (_ballSeconds > 0f ? 8f : -12f)), 0f, 1f);
        if (_upgradeSeconds == 0f && UpgradeReveal == 0f)
        {
            UpgradeIndex = -1;
        }
    }
}
