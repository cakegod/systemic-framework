using System.Numerics;

namespace GlowGarden;

/// <summary>
/// Coarse decaying food-trail field. A burst stamps a ridge from the pod to the colony that is
/// strongest at the food end, so workers with nothing in sight can climb it toward fresh Glow
/// instead of beelining across the whole map.
/// </summary>
internal sealed class GardenScent
{
    private readonly float[] _cells;
    private readonly int _columns;
    private readonly int _rows;
    private readonly float _cellSize;

    internal GardenScent(float width, float height, float cellSize)
    {
        _cellSize = MathF.Max(8, cellSize);
        _columns = Math.Max(1, (int)MathF.Ceiling(width / _cellSize));
        _rows = Math.Max(1, (int)MathF.Ceiling(height / _cellSize));
        _cells = new float[_columns * _rows];
    }

    internal void Decay(float factor)
    {
        if (factor >= 1) return;
        for (var i = 0; i < _cells.Length; i++) _cells[i] *= factor;
    }

    /// <summary>Deposits a trail that tapers from the food outward, so uphill always means "toward food".</summary>
    internal void StampTrail(Vector2 food, Vector2 home, float strength)
    {
        if (strength <= 0) return;
        var length = Vector2.Distance(food, home);
        var steps = Math.Max(1, (int)MathF.Ceiling(length / (_cellSize * 0.5f)));
        for (var step = 0; step <= steps; step++)
        {
            var progress = step / (float)steps;
            var value = strength * (1 - (0.6f * progress));
            var index = Index(Vector2.Lerp(food, home, progress));
            if (value > _cells[index]) _cells[index] = value;
        }
    }

    /// <summary>Climbs the ridge. False when the worker is already on the crest or the scent has faded.</summary>
    internal bool TrySteer(Vector2 position, float threshold, out Vector2 direction)
    {
        direction = default;
        var column = Math.Clamp((int)(position.X / _cellSize), 0, _columns - 1);
        var row = Math.Clamp((int)(position.Y / _cellSize), 0, _rows - 1);
        var best = _cells[(row * _columns) + column];
        var bestColumn = -1;
        var bestRow = -1;
        for (var offsetY = -1; offsetY <= 1; offsetY++)
        {
            for (var offsetX = -1; offsetX <= 1; offsetX++)
            {
                if (offsetX == 0 && offsetY == 0) continue;
                var nextColumn = column + offsetX;
                var nextRow = row + offsetY;
                if (nextColumn < 0 || nextColumn >= _columns || nextRow < 0 || nextRow >= _rows) continue;
                var value = _cells[(nextRow * _columns) + nextColumn];
                if (value <= best || value < threshold) continue;
                best = value;
                bestColumn = nextColumn;
                bestRow = nextRow;
            }
        }

        if (bestColumn < 0) return false;
        var centre = new Vector2((bestColumn + 0.5f) * _cellSize, (bestRow + 0.5f) * _cellSize);
        var delta = centre - position;
        var distance = delta.Length();
        if (distance < 0.001f) return false;
        direction = delta / distance;
        return true;
    }

    private int Index(Vector2 position)
    {
        var column = Math.Clamp((int)(position.X / _cellSize), 0, _columns - 1);
        var row = Math.Clamp((int)(position.Y / _cellSize), 0, _rows - 1);
        return (row * _columns) + column;
    }
}
