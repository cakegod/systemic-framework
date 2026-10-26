namespace GlowGarden;

public enum CreatureKind
{
    Burstling, Gatherer
}

public enum UpgradeTree
{
    Creatures, MagicGrowth
}

public enum UpgradeKind
{
    BurstlingWorkRate, GathererSpeed, GathererCapacity, PodGrowth, PodYield
}

public sealed partial class GardenSimulation
{
    private readonly int[] _ranks = new int[5];

    private readonly long[] _spent = new long[2];

    public long Glow { get; private set; } = 30;

    public long BankedGlow { get; private set; }

    public static UpgradeTree Tree(UpgradeKind kind) => kind switch
    {
        UpgradeKind.BurstlingWorkRate or UpgradeKind.GathererSpeed or UpgradeKind.GathererCapacity => UpgradeTree.Creatures,
        UpgradeKind.PodGrowth or UpgradeKind.PodYield => UpgradeTree.MagicGrowth,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public int Rank(UpgradeKind kind)
    {
        _ = Tree(kind);
        return _ranks[(int)kind];
    }

    public long Spent(UpgradeTree tree) => _spent[TreeIndex(tree)];

    private static int TreeIndex(UpgradeTree tree) => tree is UpgradeTree.Creatures or UpgradeTree.MagicGrowth ? (int)tree : throw new ArgumentOutOfRangeException(nameof(tree));

    public long Price(UpgradeKind kind) => checked(10L + Rank(kind) * 8L + Rank(kind) * Rank(kind) * 2L);

    public int Count(CreatureKind kind)
    {
        ValidateKind(kind);
        return _creatures.Count(c => c.Kind == kind && !c.IsCopy);
    }

    public long HirePrice(CreatureKind kind) => 20L + Count(kind) * 5L;

    private static void ValidateKind(CreatureKind kind)
    {
        if (kind is not (CreatureKind.Burstling or CreatureKind.Gatherer)) throw new ArgumentOutOfRangeException(nameof(kind));
    }

    public bool Hire(CreatureKind kind)
    {
        var price = HirePrice(kind);

        if (_creatures.Count(c => !c.IsCopy) >= MaxWorkers || Glow < price) return false;

        Glow -= price;
        _creatures.Add(new CreatureState(_nextCreatureId++, kind, Colony, 0, 0, false));
        return true;
    }

    public bool Buy(UpgradeKind kind, bool bulk = false)
    {
        var bought = false;

        do
        {
            var price = Price(kind);

            if (Rank(kind) >= 25 || Glow < price) break;

            Glow -= price;
            _spent[(int)Tree(kind)] += price;
            _ranks[(int)kind]++;
            bought = true;
        }
        while (bulk);

        return bought;
    }

    public long ResetTree(UpgradeTree tree)
    {
        var index = TreeIndex(tree);
        var refund = _spent[index];

        foreach (var kind in Enum.GetValues<UpgradeKind>()) if (Tree(kind) == tree) _ranks[(int)kind] = 0;

        _spent[index] = 0;
        Glow += refund;
        return refund;
    }
}

