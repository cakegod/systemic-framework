using System.Numerics;

namespace MoleculeSwarm;

/// <summary>
/// A shortest-path field around the rock masses, centred on the core. Every cell stores its walking
/// distance to the core and one smooth unit direction; bilinear sampling of that direction is what
/// lets the horde pour through a gate like a fluid instead of walking a grid.
/// </summary>
internal sealed class FlowField
{
    private const int Columns = Terrain.Columns;
    private const int Rows = Terrain.Rows;

    private readonly Terrain _terrain;
    private readonly float[] _distance = new float[Columns * Rows];
    private readonly Vector2[] _direction = new Vector2[Columns * Rows];
    private readonly List<int> _spawnCells = [];

    public FlowField(Terrain terrain)
    {
        _terrain = terrain;
        Rebuild();
    }

    /// <summary>Edge cells the horde can spawn on, so a wave always arrives from the map border.</summary>
    public IReadOnlyList<int> SpawnCells => _spawnCells;

    public static Vector2 CenterOf(int cell) =>
        new(((cell % Columns) + 0.5f) * Terrain.Cell, ((cell / Columns) + 0.5f) * Terrain.Cell);

    public static int CellOf(Vector2 point)
    {
        var column = Math.Clamp((int)(point.X / Terrain.Cell), 0, Columns - 1);
        var row = Math.Clamp((int)(point.Y / Terrain.Cell), 0, Rows - 1);
        return (row * Columns) + column;
    }

    public bool IsReachable(int cell) => !float.IsPositiveInfinity(_distance[cell]);

    public float DistanceAt(int cell) => _distance[cell];

    public bool IsBlocked(int column, int row)
    {
        var center = CenterOf((row * Columns) + column);
        return _terrain.Blocked(center.X, center.Y);
    }

    /// <summary>The marching direction under a point, bilinearly blended between cell centres.</summary>
    public Vector2 Sample(Vector2 point)
    {
        var gx = (point.X / Terrain.Cell) - 0.5f;
        var gy = (point.Y / Terrain.Cell) - 0.5f;
        var x0 = Math.Clamp((int)MathF.Floor(gx), 0, Columns - 1);
        var y0 = Math.Clamp((int)MathF.Floor(gy), 0, Rows - 1);
        var x1 = Math.Min(x0 + 1, Columns - 1);
        var y1 = Math.Min(y0 + 1, Rows - 1);
        var fx = Math.Clamp(gx - x0, 0f, 1f);
        var fy = Math.Clamp(gy - y0, 0f, 1f);

        var top = Vector2.Lerp(_direction[(y0 * Columns) + x0], _direction[(y0 * Columns) + x1], fx);
        var bottom = Vector2.Lerp(_direction[(y1 * Columns) + x0], _direction[(y1 * Columns) + x1], fx);
        var direction = Vector2.Lerp(top, bottom, fy);
        if (direction.LengthSquared() < 0.01f)
        {
            var fallback = Terrain.CoreCenter - point;
            return fallback.LengthSquared() > 0.01f ? Vector2.Normalize(fallback) : Vector2.UnitY;
        }

        return Vector2.Normalize(direction);
    }

    private void Rebuild()
    {
        Array.Fill(_distance, float.PositiveInfinity);
        var queue = new PriorityQueue<int, float>();
        var coreCell = CellOf(Terrain.CoreCenter);
        _distance[coreCell] = 0f;
        queue.Enqueue(coreCell, 0f);

        while (queue.TryDequeue(out var cell, out var current))
        {
            if (current > _distance[cell])
            {
                continue;
            }

            var column = cell % Columns;
            var row = cell / Columns;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    var nextColumn = column + dx;
                    var nextRow = row + dy;
                    if (nextColumn < 0 || nextRow < 0 || nextColumn >= Columns || nextRow >= Rows)
                    {
                        continue;
                    }

                    var neighbor = (nextRow * Columns) + nextColumn;
                    if (IsBlocked(nextColumn, nextRow))
                    {
                        continue;
                    }

                    var step = dx != 0 && dy != 0 ? 22.6f : 16f;
                    var candidate = current + step;
                    if (candidate >= _distance[neighbor])
                    {
                        continue;
                    }

                    _distance[neighbor] = candidate;
                    queue.Enqueue(neighbor, candidate);
                }
            }
        }

        // Average the downhill neighbours into one smooth direction per cell; bilinear sampling of
        // this field is what makes the horde pour through a lane like a fluid instead of a grid walk.
        for (var cell = 0; cell < _direction.Length; cell++)
        {
            if (float.IsPositiveInfinity(_distance[cell]))
            {
                continue;
            }

            var column = cell % Columns;
            var row = cell / Columns;
            var sum = Vector2.Zero;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0)
                    {
                        continue;
                    }

                    var nextColumn = column + dx;
                    var nextRow = row + dy;
                    if (nextColumn < 0 || nextRow < 0 || nextColumn >= Columns || nextRow >= Rows)
                    {
                        continue;
                    }

                    var neighbor = (nextRow * Columns) + nextColumn;
                    var drop = _distance[cell] - _distance[neighbor];
                    if (drop <= 0f)
                    {
                        continue;
                    }

                    var weight = drop / 16f;
                    sum += new Vector2(dx, dy) * (weight * weight);
                }
            }

            if (sum.LengthSquared() > 0.0001f)
            {
                _direction[cell] = Vector2.Normalize(sum);
            }
        }

        Smooth();

        _spawnCells.Clear();
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var edge = row < 3 || row >= Rows - 3 || column < 3 || column >= Columns - 3;
                if (!edge)
                {
                    continue;
                }

                var cell = (row * Columns) + column;
                if (!IsReachable(cell) || IsBlocked(column, row))
                {
                    continue;
                }

                _spawnCells.Add(cell);
            }
        }
    }

    /// <summary>
    /// A few box-blur passes over the direction field. Raw shortest-path gradients hug cliff walls
    /// and put a whole wave in single file; the smoothed field runs streamlines down the middle of
    /// each lane so the horde fills it instead.
    /// </summary>
    private void Smooth()
    {
        var scratch = new Vector2[_direction.Length];
        for (var pass = 0; pass < 6; pass++)
        {
            for (var row = 0; row < Rows; row++)
            {
                for (var column = 0; column < Columns; column++)
                {
                    var cell = (row * Columns) + column;
                    if (float.IsPositiveInfinity(_distance[cell]))
                    {
                        continue;
                    }

                    var sum = _direction[cell] * 5f;
                    var weight = 5f;
                    for (var dy = -1; dy <= 1; dy++)
                    {
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0)
                            {
                                continue;
                            }

                            var nextColumn = column + dx;
                            var nextRow = row + dy;
                            if (nextColumn < 0 || nextRow < 0 || nextColumn >= Columns || nextRow >= Rows)
                            {
                                continue;
                            }

                            var neighbor = (nextRow * Columns) + nextColumn;
                            if (float.IsPositiveInfinity(_distance[neighbor]))
                            {
                                continue;
                            }

                            sum += _direction[neighbor];
                            weight += 1f;
                        }
                    }

                    scratch[cell] = sum / weight;
                }
            }

            Array.Copy(scratch, _direction, _direction.Length);
        }

        for (var cell = 0; cell < _direction.Length; cell++)
        {
            if (_direction[cell].LengthSquared() > 0.0001f)
            {
                _direction[cell] = Vector2.Normalize(_direction[cell]);
            }
        }
    }
}
