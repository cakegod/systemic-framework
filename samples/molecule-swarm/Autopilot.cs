using System.Numerics;

namespace MoleculeSwarm;

/// <summary>A dumb buyer so headless runs and screenshots play themselves.</summary>
internal sealed class Autopilot(int seed)
{
    private readonly Random _random = new(seed);
    private List<int>? _candidates;

    public void Think(Sim sim)
    {
        if (sim.GameOver)
        {
            return;
        }

        var guard = 0;
        while (guard++ < 12)
        {
            if (sim.Gold >= TurretStats.Cost(TurretKind.Laser) + 150 && TryPlace(sim, TurretKind.Laser))
            {
                continue;
            }

            if (sim.Gold >= TurretStats.Cost(TurretKind.Mortar) + 80 && TryPlace(sim, TurretKind.Mortar))
            {
                continue;
            }

            if (sim.Gold >= TurretStats.Cost(TurretKind.Gun) + 40 && TryPlace(sim, TurretKind.Gun))
            {
                continue;
            }

            if (sim.Gold >= 260)
            {
                TryUpgrade(sim);
                continue;
            }

            return;
        }
    }

    private bool TryPlace(Sim sim, TurretKind kind)
    {
        var candidates = Candidates(sim);
        var start = _random.Next(candidates.Count);
        for (var step = 0; step < candidates.Count; step++)
        {
            var mount = candidates[(start + step) % candidates.Count];
            if (!sim.IsOccupied(mount) && sim.TryPlace(mount, kind))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Buildable mounts in a ring around the core, nearest first.</summary>
    private List<int> Candidates(Sim sim)
    {
        if (_candidates is not null)
        {
            return _candidates;
        }

        var found = new List<(int Mount, float Distance)>();
        for (var mount = 0; mount < TurretStats.MountCount; mount++)
        {
            if (!sim.IsBuildable(mount))
            {
                continue;
            }

            var distance = Vector2.Distance(TurretStats.MountCenter(mount), Terrain.CoreCenter);
            if (distance < 96f || distance > 340f)
            {
                continue;
            }

            found.Add((mount, distance));
        }

        found.Sort((left, right) => left.Distance.CompareTo(right.Distance));
        _candidates = [.. found.Select(entry => entry.Mount)];
        return _candidates;
    }

    private void TryUpgrade(Sim sim)
    {
        if (sim.Turrets.Count == 0)
        {
            return;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var turret = sim.Turrets.TurretAt(_random.Next(sim.Turrets.Count));
            if (sim.TryUpgrade(turret.Mount))
            {
                return;
            }
        }
    }
}
