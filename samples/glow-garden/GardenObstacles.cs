using System.Numerics;

namespace GlowGarden;

/// <summary>
/// Deterministic smooth noise field that carves organic thickets out of the garden. It derives from
/// the World's seeded draw state without consuming draws, so obstacle layout never perturbs gameplay
/// randomness. After generation every disconnected pocket is joined to the colony by a carved
/// corridor, so the free space is always one connected region. Workers slide along a thicket edge.
/// </summary>
internal sealed class GardenObstacles
{
    private const float Resolution = 8;
    private const float CoarseCell = 118;
    private const float FineCell = 44;
    private const float CoverageThreshold = 0.72f;
    private const float DegreesPerRadian = MathF.PI / 180f;
    private static readonly float[] s_deviations = [40, 80, 120, 160];

    private readonly float[] _field;
    private readonly int _columns, _rows;

    internal GardenObstacles(float width, float height, ulong seed, Vector2 keepClear, float clearRadius)
    {
        _columns = Math.Max(2, (int)MathF.Ceiling(width / Resolution));
        _rows = Math.Max(2, (int)MathF.Ceiling(height / Resolution));
        _field = new float[_columns * _rows];

        for (var row = 0; row < _rows; row++)
        {
            for (var column = 0; column < _columns; column++)
            {
                var x = (column + 0.5f) * Resolution;
                var y = (row + 0.5f) * Resolution;
                var value = (SampleNoise(x, y, CoarseCell, seed, 0x9E3779B97F4A7C15UL) * 0.74f)
                    + (SampleNoise(x, y, FineCell, seed, 0xD1B54A32D192ED03UL) * 0.26f);
                var clearance = Vector2.Distance(new Vector2(x, y), keepClear);
                if (clearance < clearRadius) value *= clearance / clearRadius;
                _field[(row * _columns) + column] = value;
            }
        }

        Connect(keepClear);
    }

    /// <summary>Bilinear sample in [0, 1]; carved corridors are part of the field, so rendering matches.</summary>
    internal float Density(float x, float y) => Sample(x, y);

    internal bool IsBlocked(Vector2 position) => IsBlocked(position.X, position.Y);

    /// <summary>
    /// Cell-quantised, not interpolated: the carved corridors are exactly the free cells, so a
    /// corridor that the flood fill proved connected is also walkable by a point worker.
    /// </summary>
    internal bool IsBlocked(float x, float y) => _field[CellIndex(x, y)] >= CoverageThreshold;

    /// <summary>
    /// Steps toward <paramref name="to"/>, following the thicket boundary when the direct line is
    /// blocked. The tangent is chosen on the side that still makes progress toward the target, so a
    /// worker traces the edge instead of grinding into it. Returns <paramref name="from"/> only when
    /// every direction is walled in.
    /// </summary>
    internal Vector2 Steer(Vector2 from, Vector2 to, ref int side)
    {
        if (side == 0 && !IsBlocked(to)) return to;
        if (ClearPath(from, to))
        {
            side = 0;
            return to;
        }

        if (IsBlocked(from))
        {
            side = 0;
            return to;
        }

        var delta = to - from;
        var length = delta.Length();
        if (length < 0.0001f) return from;
        var desired = delta / length;

        // Commit to one side of the thicket and deviate by increasing angles until a step is clear.
        // The angle is measured from the wanted direction rather than from the field gradient, so the
        // choice stays stable while the worker traces the edge instead of reversing every tick.
        if (side == 0) side = ChooseSide(from, desired, length);
        foreach (var degrees in s_deviations)
        {
            var candidate = from + (Rotate(desired, side * degrees * DegreesPerRadian) * length);
            if (!IsBlocked(candidate)) return candidate;
        }

        side = -side;
        foreach (var degrees in s_deviations)
        {
            var candidate = from + (Rotate(desired, side * degrees * DegreesPerRadian) * length);
            if (!IsBlocked(candidate)) return candidate;
        }

        var slid = Constrain(from, to);
        return slid == from ? Escape(from, length, desired) : slid;
    }

    private int ChooseSide(Vector2 from, Vector2 desired, float length)
    {
        var left = from + (Rotate(desired, 60 * DegreesPerRadian) * length);
        if (!IsBlocked(left)) return 1;
        var right = from + (Rotate(desired, -60 * DegreesPerRadian) * length);
        return IsBlocked(right) ? 1 : -1;
    }

    private static Vector2 Rotate(Vector2 direction, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector2(
            (direction.X * cos) - (direction.Y * sin),
            (direction.X * sin) + (direction.Y * cos));
    }

    /// <summary>
    /// Last resort when every direction is walled: walk to the centre of the current free cell and
    /// then toward its best free neighbour. Because the free cells are connected by construction,
    /// a worker can always leave a pinch instead of standing still forever.
    /// </summary>
    private Vector2 Escape(Vector2 from, float step, Vector2 desired)
    {
        var column = Math.Clamp((int)(from.X / Resolution), 0, _columns - 1);
        var row = Math.Clamp((int)(from.Y / Resolution), 0, _rows - 1);
        var centre = new Vector2((column + 0.5f) * Resolution, (row + 0.5f) * Resolution);
        var toCentre = centre - from;
        if (toCentre.LengthSquared() > 0.25f)
            return from + (Vector2.Normalize(toCentre) * MathF.Min(step, MathF.Sqrt(toCentre.LengthSquared())));

        var best = from;
        var bestScore = float.MinValue;
        for (var offsetRow = -1; offsetRow <= 1; offsetRow++)
        {
            for (var offsetColumn = -1; offsetColumn <= 1; offsetColumn++)
            {
                if (offsetColumn == 0 && offsetRow == 0) continue;
                var nextColumn = column + offsetColumn;
                var nextRow = row + offsetRow;
                if (!InBounds(nextColumn, nextRow) || IsBlocked(nextColumn, nextRow)) continue;
                var target = new Vector2((nextColumn + 0.5f) * Resolution, (nextRow + 0.5f) * Resolution);
                var direction = target - from;
                if (direction.LengthSquared() < 0.0001f) continue;
                var score = Vector2.Dot(Vector2.Normalize(direction), desired);
                if (score <= bestScore) continue;
                bestScore = score;
                best = from + (Vector2.Normalize(direction) * step);
            }
        }

        return best;
    }

    private bool InBounds(int column, int row) =>
        column >= 0 && column < _columns && row >= 0 && row < _rows;

    private bool IsBlocked(int column, int row) => _field[(row * _columns) + column] >= CoverageThreshold;

    private int CellIndex(float x, float y)
    {
        var column = Math.Clamp((int)(x / Resolution), 0, _columns - 1);
        var row = Math.Clamp((int)(y / Resolution), 0, _rows - 1);
        return (row * _columns) + column;
    }

    /// <summary>
    /// Line-of-sight probe. Sampled every few pixels rather than at a fixed count, because a sparse
    /// probe can miss a thin thicket and send a worker straight back into the wall it just left.
    /// </summary>
    private bool ClearPath(Vector2 from, Vector2 to)
    {
        var delta = to - from;
        var distance = delta.Length();
        var steps = Math.Clamp((int)(distance / Resolution), 1, 64);
        for (var step = 1; step <= steps; step++)
        {
            if (IsBlocked(from + (delta * (step / (float)steps)))) return false;
        }

        return true;
    }

    /// <summary>Moves along the unblocked axis when the direct step enters a thicket, so motes hug the edge.</summary>
    internal Vector2 Constrain(Vector2 from, Vector2 to)
    {
        if (IsBlocked(from) || !IsBlocked(to)) return to;
        var slideX = new Vector2(to.X, from.Y);
        if (!IsBlocked(slideX)) return slideX;
        var slideY = new Vector2(from.X, to.Y);
        return IsBlocked(slideY) ? from : slideY;
    }

    /// <summary>Flood fills the free space and carves a corridor into every pocket the colony cannot reach.</summary>
    private void Connect(Vector2 start)
    {
        var startColumn = Math.Clamp((int)(start.X / Resolution), 0, _columns - 1);
        var startRow = Math.Clamp((int)(start.Y / Resolution), 0, _rows - 1);
        _field[(startRow * _columns) + startColumn] = 0;
        var reachable = new bool[_field.Length];
        var queue = new Queue<int>(_field.Length / 4);
        while (true)
        {
            Flood(startColumn, startRow, reachable, queue);
            var pocket = -1;
            for (var cell = 0; cell < _field.Length; cell++)
            {
                if (_field[cell] < CoverageThreshold && !reachable[cell])
                {
                    pocket = cell;
                    break;
                }
            }

            if (pocket < 0) return;
            Carve(pocket, NearestReachable(pocket, reachable));
        }
    }

    private void Flood(int startColumn, int startRow, bool[] reachable, Queue<int> queue)
    {
        Array.Clear(reachable);
        queue.Clear();
        var start = (startRow * _columns) + startColumn;
        reachable[start] = true;
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            var column = cell % _columns;
            var row = cell / _columns;
            Visit(column - 1, row);
            Visit(column + 1, row);
            Visit(column, row - 1);
            Visit(column, row + 1);
        }

        void Visit(int column, int row)
        {
            if (column < 0 || column >= _columns || row < 0 || row >= _rows) return;
            var cell = (row * _columns) + column;
            if (reachable[cell] || _field[cell] >= CoverageThreshold) return;
            reachable[cell] = true;
            queue.Enqueue(cell);
        }
    }

    private int NearestReachable(int pocket, bool[] reachable)
    {
        var pocketColumn = pocket % _columns;
        var pocketRow = pocket / _columns;
        var best = pocket;
        var bestDistance = float.MaxValue;
        for (var cell = 0; cell < _field.Length; cell++)
        {
            if (!reachable[cell]) continue;
            var column = cell % _columns;
            var row = cell / _columns;
            var distance = ((column - pocketColumn) * (column - pocketColumn))
                + ((row - pocketRow) * (row - pocketRow));
            if (distance >= bestDistance) continue;
            best = cell;
            bestDistance = distance;
        }

        return best;
    }

    private void Carve(int from, int to)
    {
        var column = from % _columns;
        var row = from / _columns;
        var targetColumn = to % _columns;
        var targetRow = to / _columns;
        var deltaColumn = Math.Abs(targetColumn - column);
        var deltaRow = Math.Abs(targetRow - row);
        var stepColumn = column < targetColumn ? 1 : -1;
        var stepRow = row < targetRow ? 1 : -1;
        var error = deltaColumn - deltaRow;
        while (true)
        {
            Open(column, row);
            Open(column + 1, row);
            Open(column, row + 1);
            if (column == targetColumn && row == targetRow) return;
            var doubled = 2 * error;
            if (doubled > -deltaRow)
            {
                error -= deltaRow;
                column += stepColumn;
            }

            if (doubled < deltaColumn)
            {
                error += deltaColumn;
                row += stepRow;
            }
        }
    }

    private void Open(int column, int row)
    {
        if (column < 0 || column >= _columns || row < 0 || row >= _rows) return;
        _field[(row * _columns) + column] = 0;
    }

    private float Sample(float x, float y)
    {
        var scaledX = Math.Clamp((x / Resolution) - 0.5f, 0, _columns - 1);
        var scaledY = Math.Clamp((y / Resolution) - 0.5f, 0, _rows - 1);
        var left = Math.Min((int)scaledX, _columns - 1);
        var top = Math.Min((int)scaledY, _rows - 1);
        var right = Math.Min(left + 1, _columns - 1);
        var bottom = Math.Min(top + 1, _rows - 1);
        var across = scaledX - left;
        var down = scaledY - top;
        var upper = Lerp(_field[(top * _columns) + left], _field[(top * _columns) + right], across);
        var lower = Lerp(_field[(bottom * _columns) + left], _field[(bottom * _columns) + right], across);
        return Lerp(upper, lower, down);
    }

    private static float SampleNoise(float x, float y, float cellSize, ulong seed, ulong salt)
    {
        var scaledX = MathF.Max(0, x) / cellSize;
        var scaledY = MathF.Max(0, y) / cellSize;
        var left = (int)scaledX;
        var top = (int)scaledY;
        var across = Smooth(scaledX - left);
        var down = Smooth(scaledY - top);
        var upper = Lerp(Hash(left, top, seed, salt), Hash(left + 1, top, seed, salt), across);
        var lower = Lerp(Hash(left, top + 1, seed, salt), Hash(left + 1, top + 1, seed, salt), across);
        return Lerp(upper, lower, down);
    }

    private static float Hash(int x, int y, ulong seed, ulong salt)
    {
        var value = seed ^ salt;
        value ^= (ulong)(uint)x * 0x9E3779B97F4A7C15UL;
        value ^= (ulong)(uint)y * 0xC2B2AE3D27D4EB4FUL;
        value += 0x165667B19E3779F9UL;
        value ^= value >> 33;
        value *= 0xFF51AFD7ED558CCDUL;
        value ^= value >> 33;
        value *= 0xC4CEB9FE1A85EC53UL;
        value ^= value >> 33;
        return (value >> 40) / (float)(1UL << 24);
    }

    private static float Smooth(float amount) => amount * amount * (3 - (2 * amount));

    private static float Lerp(float start, float end, float amount) => start + ((end - start) * amount);
}
