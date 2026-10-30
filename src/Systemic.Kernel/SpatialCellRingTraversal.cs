using System.Numerics;

namespace Systemic.Kernel;

/// <summary>
/// Visits each cell of a bounded grid once in expanding Chebyshev rings. Before entering another
/// ring, the minimum distance to either unvisited axis provides a conservative bound for every
/// unvisited cell. Equality is retained so row-ID ties remain visible.
/// </summary>
/// <remarks>
/// Both bounded grids in the kernel walk cells through this type, so nearest-neighbour pruning and
/// tie behaviour are defined once rather than once per storage.
/// </remarks>
internal struct SpatialCellRingTraversal
{
    private readonly RingAxis _x;
    private readonly RingAxis _y;
    private readonly int _cellsX;
    private int _ring;
    private AxisCellRange _currentX;
    private AxisCellRange _previousY;
    private int _x0, _x1, _xCount, _y0, _y1, _yCount;
    private int _phase, _outer, _inner;
    private bool _finished;

    public SpatialCellRingTraversal(Vector2 center, Bounds2 bounds, float cellSize,
        int cellsX, int cellsY, bool wrapX, bool wrapY)
    {
        _x = new RingAxis(center.X, bounds.Left, bounds.Right, bounds.HalfWidth * 2d, cellSize, cellsX, wrapX);
        _y = new RingAxis(center.Y, bounds.Top, bounds.Bottom, bounds.HalfHeight * 2d, cellSize, cellsY, wrapY);
        _cellsX = cellsX;
        _ring = -1;
        _currentX = default;
        _previousY = default;
        _x0 = _x1 = _xCount = _y0 = _y1 = _yCount = 0;
        _phase = _outer = _inner = 0;
        _finished = false;
    }

    public bool MoveNext(double maximumDistanceSquared, out int cell)
    {
        while (!_finished)
        {
            if (_ring >= 0 && _phase == 0)
            {
                if (_outer < _yCount)
                {
                    var y = _outer == 0 ? _y0 : _y1;
                    var x = _currentX.At(_inner, _x.Count, _x.Wrap);
                    if (++_inner == _currentX.Count)
                    {
                        _inner = 0;
                        _outer++;
                    }
                    cell = y * _cellsX + x;
                    return true;
                }
                _phase = 1;
                _outer = _inner = 0;
            }

            if (_ring >= 0 && _phase == 1 && _xCount != 0 && _outer < _previousY.Count)
            {
                var y = _previousY.At(_outer, _y.Count, _y.Wrap);
                var x = _inner == 0 ? _x0 : _x1;
                if (++_inner == _xCount)
                {
                    _inner = 0;
                    _outer++;
                }
                cell = y * _cellsX + x;
                return true;
            }

            if (_ring >= 0)
            {
                var xDistance = _x.DistanceToOutside(_ring);
                var yDistance = _y.DistanceToOutside(_ring);
                var outsideDistance = Math.Min(xDistance, yDistance);
                if (outsideDistance * outsideDistance > maximumDistanceSquared)
                {
                    _finished = true;
                    break;
                }
            }

            _ring++;
            _x.GetNewCells(_ring, out _x0, out _x1, out _xCount);
            _y.GetNewCells(_ring, out _y0, out _y1, out _yCount);
            if (_xCount == 0 && _yCount == 0)
            {
                _finished = true;
                break;
            }
            _currentX = _x.Window(_ring);
            _previousY = _y.Window(_ring - 1);
            _phase = _outer = _inner = 0;
        }

        cell = -1;
        return false;
    }

    private readonly struct RingAxis
    {
        private const double ErrorFactor = 16d * 2.2204460492503131e-16;
        private readonly double _center, _minimum, _maximum, _size, _cellSize, _error;
        private readonly int _centerCell;
        public int Count { get; }
        public bool Wrap { get; }

        public RingAxis(double center, double minimum, double maximum, double size,
            double cellSize, int count, bool wrap)
        {
            _center = center;
            _minimum = minimum;
            _maximum = maximum;
            _size = size;
            _cellSize = cellSize;
            Count = count;
            Wrap = wrap;
            // Rounded interval and modulo operations must never overstate a lower bound.
            // If coordinates dwarf the world, the error deliberately disables pruning.
            _error = (Math.Abs(center) + Math.Abs(minimum) + Math.Abs(maximum) + size + cellSize) * ErrorFactor;
            var offset = center - minimum;
            if (wrap)
            {
                offset = SpatialGrid.Mod(offset % size, size);
            }
            var candidate = offset <= 0 ? 0 : offset >= size ? count - 1 : (int)(offset / cellSize);
            _centerCell = Math.Clamp(candidate, 0, count - 1);
        }

        public AxisCellRange Window(int ring)
        {
            if (ring < 0)
            {
                return default;
            }

            if (Wrap)
            {
                if ((2L * ring) + 1 >= Count)
                {
                    return new AxisCellRange(0, Count);
                }

                var start = _centerCell - ring;
                if (start < 0)
                {
                    start += Count;
                }

                return new AxisCellRange(start, (2 * ring) + 1);
            }
            var first = Math.Max(0, _centerCell - ring);
            var last = Math.Min(Count - 1, _centerCell + ring);
            return new AxisCellRange(first, last - first + 1);
        }

        public void GetNewCells(int ring, out int first, out int second, out int count)
        {
            first = second = -1;
            if (ring == 0)
            {
                first = _centerCell;
                count = 1;
                return;
            }
            if (Wrap && ring > Count / 2)
            {
                count = 0;
                return;
            }
            var left = _centerCell - ring;
            var right = _centerCell + ring;
            if (Wrap)
            {
                if (left < 0)
                {
                    left += Count;
                }

                if (right >= Count)
                {
                    right -= Count;
                }
            }
            count = 0;
            if (left >= 0 && left < Count)
            {
                first = left;
                count = 1;
            }
            if (right >= 0 && right < Count && right != left)
            {
                if (count == 0)
                {
                    first = right;
                }
                else
                {
                    second = right;
                }

                count++;
            }
        }

        public double DistanceToOutside(int ring)
        {
            GetNewCells(ring + 1, out var first, out var second, out var count);
            if (count == 0)
            {
                return double.PositiveInfinity;
            }

            var distance = DistanceToCell(first);
            if (count == 2)
            {
                distance = Math.Min(distance, DistanceToCell(second));
            }

            return Math.Max(0d, distance - _error);
        }

        private double DistanceToCell(int cell)
        {
            var start = _minimum + (cell * _cellSize);
            var end = cell == Count - 1
                ? Math.Max(_maximum, _minimum + _size)
                : Math.Min(_minimum + ((cell + 1) * _cellSize), Math.Max(_maximum, _minimum + _size));
            if (!Wrap)
            {
                return IntervalDistance(_center, start, end);
            }

            var offset = SpatialGrid.Mod((_center - _minimum) % _size, _size);
            var point = _minimum + offset;
            return Math.Min(IntervalDistance(point, start, end),
                Math.Min(IntervalDistance(point - _size, start, end), IntervalDistance(point + _size, start, end)));
        }

        private static double IntervalDistance(double value, double start, double end) =>
            value < start ? start - value : value > end ? value - end : 0d;
    }
}
