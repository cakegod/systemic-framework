using System.Collections.Immutable;

namespace Systemic.Presentation;

/// <summary>Bounded LRU of generated source-pixel buffers; adapters can convert cache misses to engine textures.</summary>
public sealed class VisualVariantCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<CacheKey, LinkedListNode<CacheEntry>> _entries = [];
    private readonly LinkedList<CacheEntry> _leastRecentlyUsed = [];
    private long _cachedBytes;

    public VisualVariantCache(long maximumBytes = 8 * 1024 * 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        MaximumBytes = maximumBytes;
    }

    public long MaximumBytes { get; }

    public long CachedBytes
    {
        get
        {
            lock (_gate)
            {
                return _cachedBytes;
            }
        }
    }

    public int CachedCount
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    public PixelBuffer GetOrCreate(VisualRecipe recipe, ulong variantSeed = 0, string? visualState = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        var key = new CacheKey(ProceduralRasterizer.GeneratorVersion, recipe, variantSeed, visualState);
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached))
            {
                Touch(cached);
                return cached.Value.Pixels;
            }
        }

        var generated = ProceduralRasterizer.Rasterize(recipe, variantSeed, visualState);
        var bytes = generated.Pixels.Length;
        if (bytes > MaximumBytes)
        {
            return generated;
        }

        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var concurrent))
            {
                Touch(concurrent);
                return concurrent.Value.Pixels;
            }

            while (_cachedBytes > MaximumBytes - bytes && _leastRecentlyUsed.Last is { } oldest)
            {
                _entries.Remove(oldest.Value.Key);
                _cachedBytes -= oldest.Value.ByteCount;
                _leastRecentlyUsed.RemoveLast();
            }

            var entry = new CacheEntry(key, generated, bytes);
            _entries.Add(key, _leastRecentlyUsed.AddFirst(entry));
            _cachedBytes += bytes;
            return generated;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _leastRecentlyUsed.Clear();
            _cachedBytes = 0;
        }
    }

    private void Touch(LinkedListNode<CacheEntry> node)
    {
        _leastRecentlyUsed.Remove(node);
        _leastRecentlyUsed.AddFirst(node);
    }

    private readonly struct CacheKey(int version, VisualRecipe recipe, ulong variantSeed, string? visualState) : IEquatable<CacheKey>
    {
        private readonly int _version = version;
        private readonly VisualRecipe _recipe = recipe;
        private readonly ulong _variantSeed = variantSeed;
        private readonly string? _visualState = visualState;

        public bool Equals(CacheKey other) =>
            _version == other._version
            && _variantSeed == other._variantSeed
            && string.Equals(_visualState, other._visualState, StringComparison.Ordinal)
            && string.Equals(_recipe.Id, other._recipe.Id, StringComparison.Ordinal)
            && string.Equals(_recipe.Kind, other._recipe.Kind, StringComparison.Ordinal)
            && _recipe.Seed == other._recipe.Seed
            && _recipe.CanvasWidth == other._recipe.CanvasWidth
            && _recipe.CanvasHeight == other._recipe.CanvasHeight
            && string.Equals(_recipe.ImportedAssetId, other._recipe.ImportedAssetId, StringComparison.Ordinal)
            && _recipe.ImportedMode == other._recipe.ImportedMode
            && _recipe.Palette.AsSpan().SequenceEqual(other._recipe.Palette.AsSpan())
            && _recipe.Layers.AsSpan().SequenceEqual(other._recipe.Layers.AsSpan())
            && PartsEqual(_recipe.Parts, other._recipe.Parts);

        public override bool Equals(object? obj) => obj is CacheKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(_version);
            hash.Add(_variantSeed);
            hash.Add(_visualState, StringComparer.Ordinal);
            hash.Add(_recipe.Id, StringComparer.Ordinal);
            hash.Add(_recipe.Kind, StringComparer.Ordinal);
            hash.Add(_recipe.Seed);
            hash.Add(_recipe.CanvasWidth);
            hash.Add(_recipe.CanvasHeight);
            hash.Add(_recipe.ImportedAssetId, StringComparer.Ordinal);
            hash.Add(_recipe.ImportedMode);
            foreach (var color in _recipe.Palette)
            {
                hash.Add(color, StringComparer.Ordinal);
            }

            foreach (var layer in _recipe.Layers)
            {
                hash.Add(layer);
            }

            if (!_recipe.Parts.IsDefaultOrEmpty)
            {
                foreach (var part in _recipe.Parts)
                {
                    hash.Add(part.X);
                    hash.Add(part.Y);
                    hash.Add(part.Scale);
                    AddRecipeHash(ref hash, part.Recipe);
                }
            }

            return hash.ToHashCode();
        }

        private static bool PartsEqual(ImmutableArray<VisualPart> left, ImmutableArray<VisualPart> right)
        {
            if (left.IsDefaultOrEmpty || right.IsDefaultOrEmpty)
            {
                return left.IsDefaultOrEmpty && right.IsDefaultOrEmpty;
            }

            if (left.Length != right.Length)
            {
                return false;
            }

            for (var index = 0; index < left.Length; index++)
            {
                var leftPart = left[index];
                var rightPart = right[index];
                if (leftPart.X != rightPart.X || leftPart.Y != rightPart.Y || leftPart.Scale != rightPart.Scale
                    || !RecipeEquals(leftPart.Recipe, rightPart.Recipe))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool RecipeEquals(VisualRecipe left, VisualRecipe right) =>
            string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && string.Equals(left.Kind, right.Kind, StringComparison.Ordinal)
            && left.Seed == right.Seed
            && left.CanvasWidth == right.CanvasWidth
            && left.CanvasHeight == right.CanvasHeight
            && left.Palette.AsSpan().SequenceEqual(right.Palette.AsSpan())
            && left.Layers.AsSpan().SequenceEqual(right.Layers.AsSpan());

        private static void AddRecipeHash(ref HashCode hash, VisualRecipe recipe)
        {
            hash.Add(recipe.Id, StringComparer.Ordinal);
            hash.Add(recipe.Kind, StringComparer.Ordinal);
            hash.Add(recipe.Seed);
            hash.Add(recipe.CanvasWidth);
            hash.Add(recipe.CanvasHeight);
            foreach (var color in recipe.Palette)
            {
                hash.Add(color, StringComparer.Ordinal);
            }

            foreach (var layer in recipe.Layers)
            {
                hash.Add(layer);
            }
        }
    }

    private sealed record CacheEntry(CacheKey Key, PixelBuffer Pixels, int ByteCount);
}
