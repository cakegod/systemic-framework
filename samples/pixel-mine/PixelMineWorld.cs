namespace PixelMine;

public enum Material : byte
{
    Air = 0,
    Dirt = 1,
    DirtDark = 2,
    Clay = 3,
    Stone = 4,
    Deep = 5,
    Prism = 6,
    Gold = 7
}

/// <summary>The mineable field. Ten million cells, generated in 64x64 chunks on first touch.</summary>
public sealed class PixelMineWorld
{
    public const int Width = 4000;
    public const int Height = 2500;
    public const int CellCount = Width * Height;

    private const int ChunkShift = 6;
    private const int ChunkSize = 1 << ChunkShift;
    private const int ChunksX = (Width + ChunkSize - 1) >> ChunkShift;
    private const int ChunksY = (Height + ChunkSize - 1) >> ChunkShift;
    private const int StarterPrisms = 6;

    private readonly byte[] _cells = new byte[CellCount];
    private readonly byte[] _damage = new byte[CellCount];
    private readonly bool[] _chunkReady = new bool[ChunksX * ChunksY];
    private readonly int _seed;

    public PixelMineWorld(int seed)
    {
        _seed = seed;
        SpawnX = Width / 2;
        SpawnY = Height / 2;
        GoldX = Math.Clamp(SpawnX + (int)((Unit(11, 0, seed) - 0.5f) * 700f), 0, Width - 1);
        GoldY = Math.Clamp((int)(Height * (0.82f + (Unit(12, 0, seed) * 0.1f))), 0, Height - 1);

        CarveSpawnArea();
        PlaceGold();
        PlaceStarterPrisms();
    }

    public int SpawnX { get; }

    public int SpawnY { get; }

    public int GoldX { get; }

    public int GoldY { get; }

    public static int Hardness(Material material) => material switch
    {
        Material.Dirt or Material.DirtDark => 1,
        Material.Clay => 2,
        Material.Stone => 3,
        Material.Deep => 4,
        Material.Prism => 1,
        Material.Gold => 6,
        _ => 0
    };

    public static int Yield(Material material) => material switch
    {
        Material.Dirt or Material.DirtDark => 1,
        Material.Clay => 2,
        Material.Stone => 3,
        Material.Deep => 5,
        _ => 0
    };

    public Material MaterialAt(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height)
        {
            return Material.Air;
        }

        EnsureChunk(x >> ChunkShift, y >> ChunkShift);
        return (Material)_cells[(y * Width) + x];
    }

    public byte DamageAt(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height)
        {
            return 0;
        }

        EnsureChunk(x >> ChunkShift, y >> ChunkShift);
        return _damage[(y * Width) + x];
    }

    public bool IsSolid(int x, int y) => MaterialAt(x, y) != Material.Air;

    public void EnsureRegion(int left, int top, int width, int height)
    {
        var x0 = Math.Clamp(left, 0, Width - 1) >> ChunkShift;
        var y0 = Math.Clamp(top, 0, Height - 1) >> ChunkShift;
        var x1 = Math.Clamp(left + width - 1, 0, Width - 1) >> ChunkShift;
        var y1 = Math.Clamp(top + height - 1, 0, Height - 1) >> ChunkShift;
        for (var cy = y0; cy <= y1; cy++)
        {
            for (var cx = x0; cx <= x1; cx++)
            {
                EnsureChunk(cx, cy);
            }
        }
    }

    public void EnsureAll()
    {
        for (var cy = 0; cy < ChunksY; cy++)
        {
            for (var cx = 0; cx < ChunksX; cx++)
            {
                EnsureChunk(cx, cy);
            }
        }
    }

    public bool TryDamage(int x, int y, int amount, out Material material)
    {
        material = Material.Air;
        if ((uint)x >= Width || (uint)y >= Height)
        {
            return false;
        }

        EnsureChunk(x >> ChunkShift, y >> ChunkShift);
        var index = (y * Width) + x;
        material = (Material)_cells[index];
        if (material == Material.Air)
        {
            return false;
        }

        var total = _damage[index] + Math.Max(1, amount);
        if (total < Hardness(material))
        {
            _damage[index] = (byte)total;
            return false;
        }

        _cells[index] = (byte)Material.Air;
        _damage[index] = 0;
        return true;
    }

    private void EnsureChunk(int cx, int cy)
    {
        var chunkIndex = (cy * ChunksX) + cx;
        if (_chunkReady[chunkIndex])
        {
            return;
        }

        _chunkReady[chunkIndex] = true;
        var x0 = cx << ChunkShift;
        var y0 = cy << ChunkShift;
        var x1 = Math.Min(x0 + ChunkSize, Width);
        var y1 = Math.Min(y0 + ChunkSize, Height);

        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var index = (y * Width) + x;
                var material = BaseMaterial(x, y);
                if (Unit(x, y, _seed ^ 0x5BD1E995) < 0.00015f)
                {
                    material = Material.Prism;
                }

                _cells[index] = (byte)material;
                _damage[index] = 0;
            }
        }
    }

    private Material BaseMaterial(int x, int y)
    {
        var pick = Unit(x, y, _seed ^ 0x51ED270B);
        if (y < Height * 0.28f)
        {
            return pick < 0.55f ? Material.Dirt : pick < 0.85f ? Material.DirtDark : Material.Clay;
        }

        var vein = Noise(x, y, 21, 0x4F1BBC7A);
        var pocket = Noise(x, y, 3, 0x2A63F1C9);
        if (y < Height * 0.74f)
        {
            if (vein > 0.64f || pocket > 0.72f)
            {
                return Material.Clay;
            }

            return vein < 0.3f || pocket < 0.24f ? Material.Deep : Material.Stone;
        }

        return vein < 0.34f || pocket > 0.7f ? Material.Stone : Material.Deep;
    }

    private void CarveSpawnArea()
    {
        for (var dy = -4; dy <= 4; dy++)
        {
            for (var dx = -6; dx <= 6; dx++)
            {
                var nx = dx / 6f;
                var ny = dy / 4f;
                var edge = (Noise(SpawnX + dx, SpawnY + dy, 3, 0x7F4A7C15) - 0.5f)
                    + ((Noise(SpawnX + dx, SpawnY + dy, 2, 0x13579BDF) - 0.5f) * 0.5f);
                if ((nx * nx) + (ny * ny) + (edge * 0.9f) < 1f)
                {
                    SetAir(SpawnX + dx, SpawnY + dy);
                }
            }
        }
    }

    private void PlaceGold()
    {
        EnsureChunk(GoldX >> ChunkShift, GoldY >> ChunkShift);
        var index = (GoldY * Width) + GoldX;
        _cells[index] = (byte)Material.Gold;
        _damage[index] = 0;
    }

    private void PlaceStarterPrisms()
    {
        for (var prism = 0; prism < StarterPrisms; prism++)
        {
            var angle = (prism * MathF.Tau / StarterPrisms) + 0.4f;
            var radius = 10f + (Unit(prism, 21, _seed) * 6f);
            for (var attempt = 0; attempt < 12; attempt++)
            {
                var distance = radius + (attempt * 9f);
                var x = Math.Clamp((int)(SpawnX + (MathF.Cos(angle) * distance)), 0, Width - 1);
                var y = Math.Clamp((int)(SpawnY + (MathF.Sin(angle) * distance)), 0, Height - 1);
                EnsureChunk(x >> ChunkShift, y >> ChunkShift);
                var index = (y * Width) + x;
                if ((Material)_cells[index] == Material.Air)
                {
                    continue;
                }

                _cells[index] = (byte)Material.Prism;
                _damage[index] = 0;
                break;
            }
        }
    }

    private void SetAir(int x, int y)
    {
        if ((uint)x >= Width || (uint)y >= Height)
        {
            return;
        }

        EnsureChunk(x >> ChunkShift, y >> ChunkShift);
        var index = (y * Width) + x;
        _cells[index] = (byte)Material.Air;
        _damage[index] = 0;
    }

    private float Noise(int x, int y, int scale, int salt)
    {
        var px = x / (float)scale;
        var py = y / (float)scale;
        var x0 = (int)MathF.Floor(px);
        var y0 = (int)MathF.Floor(py);
        var fx = px - x0;
        var fy = py - y0;
        fx = fx * fx * (3f - (2f * fx));
        fy = fy * fy * (3f - (2f * fy));

        var a = Unit(x0, y0, _seed ^ salt);
        var b = Unit(x0 + 1, y0, _seed ^ salt);
        var c = Unit(x0, y0 + 1, _seed ^ salt);
        var d = Unit(x0 + 1, y0 + 1, _seed ^ salt);
        var top = a + ((b - a) * fx);
        var bottom = c + ((d - c) * fx);
        return top + ((bottom - top) * fy);
    }

    private static float Unit(int x, int y, int seed)
    {
        var hash = unchecked(((uint)x * 374761393u) + ((uint)y * 668265263u) + ((uint)seed * 2246822519u));
        hash ^= hash >> 13;
        hash *= 1274126177u;
        hash ^= hash >> 16;
        return (hash & 0xFFFFFF) / 16777216f;
    }
}
