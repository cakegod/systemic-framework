using System.Collections.ObjectModel;
using Systemic.Kernel;

namespace Systemic.Gameplay2D;

/// <summary>
/// Deterministic, bounded, four-way navigation over immutable integer cell masks. Neighbor order
/// is north, east, south, west. All costs and distances are integers.
/// </summary>
public static class DeterministicNavigation2D
{
    private static readonly ReadOnlyCollection<GridCell2D> EmptyPath = Array.AsReadOnly(Array.Empty<GridCell2D>());

    /// <summary>
    /// Finds a shortest four-way path, including start and target. Invalid, blocked, or disconnected
    /// endpoints yield an empty path. A* prioritizes lowest total estimate, then lowest remaining
    /// Manhattan distance, then insertion order; equal-cost predecessors are not replaced.
    /// </summary>
    public static IReadOnlyList<GridCell2D> FindPath(StaticBlockingField2D field, GridCell2D start, GridCell2D target)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!IsWalkable(field, start) || !IsWalkable(field, target))
        {
            return EmptyPath;
        }

        var count = field.Width * field.Height;
        var startIndex = Index(field, start);
        var targetIndex = Index(field, target);
        var costs = new int[count];
        var parents = new int[count];
        Array.Fill(costs, -1);
        Array.Fill(parents, -1);
        costs[startIndex] = 0;

        var open = new PriorityQueue<(int Index, int Cost), (int Total, int Remaining, long Order)>();
        long order = 0;
        var initialEstimate = ManhattanDistance(start, target);
        open.Enqueue((startIndex, 0), (initialEstimate, initialEstimate, order++));

        while (open.TryDequeue(out var item, out _))
        {
            if (costs[item.Index] != item.Cost)
            {
                continue;
            }

            if (item.Index == targetIndex)
            {
                var path = new List<GridCell2D>();
                for (var index = targetIndex; index >= 0; index = parents[index])
                {
                    path.Add(Cell(field, index));
                }

                path.Reverse();
                return path.AsReadOnly();
            }

            var current = Cell(field, item.Index);
            AddNeighbor(new GridCell2D(current.X, current.Y - 1));
            AddNeighbor(new GridCell2D(current.X + 1, current.Y));
            AddNeighbor(new GridCell2D(current.X, current.Y + 1));
            AddNeighbor(new GridCell2D(current.X - 1, current.Y));

            void AddNeighbor(GridCell2D neighbor)
            {
                if (!IsWalkable(field, neighbor))
                {
                    return;
                }

                var index = Index(field, neighbor);
                var cost = item.Cost + 1;
                if (costs[index] >= 0 && costs[index] <= cost)
                {
                    return;
                }

                costs[index] = cost;
                parents[index] = item.Index;
                var remaining = ManhattanDistance(neighbor, target);
                open.Enqueue((index, cost), (cost + remaining, remaining, order++));
            }
        }

        return EmptyPath;
    }

    /// <summary>
    /// Builds a reusable shortest-distance flow field toward a target. Returns null when the target
    /// is blocked or outside the finite field. The field is independent of future callers and may
    /// be queried from any number of starting cells.
    /// </summary>
    public static NavigationFlowField2D? CreateFlowField(StaticBlockingField2D field, GridCell2D target)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (!IsWalkable(field, target))
        {
            return null;
        }

        var distances = new int[field.Width * field.Height];
        Array.Fill(distances, -1);
        var queue = new int[distances.Length];
        var head = 0;
        var tail = 0;
        var targetIndex = Index(field, target);
        distances[targetIndex] = 0;
        queue[tail++] = targetIndex;
        while (head < tail)
        {
            var currentIndex = queue[head++];
            var current = Cell(field, currentIndex);
            AddNeighbor(new GridCell2D(current.X, current.Y - 1));
            AddNeighbor(new GridCell2D(current.X + 1, current.Y));
            AddNeighbor(new GridCell2D(current.X, current.Y + 1));
            AddNeighbor(new GridCell2D(current.X - 1, current.Y));

            void AddNeighbor(GridCell2D neighbor)
            {
                if (!IsWalkable(field, neighbor))
                {
                    return;
                }

                var index = Index(field, neighbor);
                if (distances[index] < 0)
                {
                    distances[index] = distances[currentIndex] + 1;
                    queue[tail++] = index;
                }
            }
        }

        return new NavigationFlowField2D(field, target, distances);
    }

    internal static bool IsWalkable(StaticBlockingField2D field, GridCell2D cell) =>
        field.IsInBounds(cell) && !field.IsBlocked(cell);

    internal static int Index(StaticBlockingField2D field, GridCell2D cell) => (cell.Y * field.Width) + cell.X;

    private static GridCell2D Cell(StaticBlockingField2D field, int index) => new(index % field.Width, index / field.Width);

    private static int ManhattanDistance(GridCell2D first, GridCell2D second) =>
        Math.Abs(first.X - second.X) + Math.Abs(first.Y - second.Y);
}

/// <summary>
/// Immutable four-way shortest distances to a target. For equal-cost moves the first direction in
/// north, east, south, west order is selected. It never routes outside the source field.
/// </summary>
public sealed class NavigationFlowField2D
{
    private readonly StaticBlockingField2D _field;
    private readonly int[] _distances;
    private readonly ReadOnlyCollection<int> _distanceSnapshot;

    internal NavigationFlowField2D(StaticBlockingField2D field, GridCell2D target, int[] distances)
    {
        _field = field;
        Target = target;
        _distances = distances;
        _distanceSnapshot = Array.AsReadOnly(distances);
    }

    public GridCell2D Target { get; }

    /// <summary>Read-only row-major distances; -1 marks blocked or unreachable cells.</summary>
    public IReadOnlyList<int> Distances => _distanceSnapshot;

    /// <summary>Returns false and a distance of -1 for an invalid, blocked, or unreachable cell.</summary>
    public bool TryGetDistance(GridCell2D cell, out int distance)
    {
        distance = _field.IsInBounds(cell) ? _distances[DeterministicNavigation2D.Index(_field, cell)] : -1;
        return distance >= 0;
    }

    /// <summary>
    /// Returns the first neighbor one step closer to the target. Returns false for invalid or
    /// unreachable cells and for the target itself.
    /// </summary>
    public bool TryGetNextCell(GridCell2D cell, out GridCell2D next)
    {
        next = default;
        if (!TryGetDistance(cell, out var distance) || distance == 0)
        {
            return false;
        }

        var north = new GridCell2D(cell.X, cell.Y - 1);
        var east = new GridCell2D(cell.X + 1, cell.Y);
        var south = new GridCell2D(cell.X, cell.Y + 1);
        var west = new GridCell2D(cell.X - 1, cell.Y);
        if (TryGetDistance(north, out var northDistance) && northDistance == distance - 1)
        {
            next = north;
        }
        else if (TryGetDistance(east, out var eastDistance) && eastDistance == distance - 1)
        {
            next = east;
        }
        else if (TryGetDistance(south, out var southDistance) && southDistance == distance - 1)
        {
            next = south;
        }
        else if (TryGetDistance(west, out var westDistance) && westDistance == distance - 1)
        {
            next = west;
        }
        else
        {
            return false;
        }

        return true;
    }
}
