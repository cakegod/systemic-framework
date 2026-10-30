using System.Collections.Immutable;

namespace Systemic.Kernel;

/// <summary>Runs synchronous serial work with a stack-scoped view of population rows.</summary>
/// <typeparam name="TKind">The population schema.</typeparam>
/// <typeparam name="TState">Caller-owned state shared with the callback.</typeparam>
/// <param name="state">The caller-owned state.</param>
/// <param name="scope">A view whose reads and writes expire when the callback returns.</param>
public delegate void PopulationAccessAction<TKind, TState>(
    TState state,
    scoped ref PopulationAccessScope<TKind> scope)
    where TKind : IPopulationKind;

/// <summary>Runs synchronous initialization work over caller-owned input and scoped population rows.</summary>
/// <typeparam name="TKind">The population schema.</typeparam>
/// <typeparam name="TInput">The unmanaged input row type.</typeparam>
/// <typeparam name="TState">Caller-owned state shared with the callback.</typeparam>
/// <param name="input">Read-only input whose lifetime is limited to the callback.</param>
/// <param name="state">Caller-owned state passed to the callback.</param>
/// <param name="scope">A view whose reads and writes expire when the callback returns.</param>
public delegate void PopulationInputAccessAction<TKind, TInput, TState>(
    scoped ReadOnlySpan<TInput> input,
    TState state,
    scoped ref PopulationAccessScope<TKind> scope)
    where TKind : IPopulationKind
    where TInput : unmanaged;

/// <summary>Runs synchronous work for one scheduler-assigned population partition.</summary>
/// <typeparam name="TKind">The population schema.</typeparam>
/// <typeparam name="TState">Caller-owned state shared with the callbacks.</typeparam>
/// <param name="state">The caller-owned state.</param>
/// <param name="partition">The stable logical partition index.</param>
/// <param name="scope">The unforgeable partition access capability, valid only during this callback.</param>
public delegate void PopulationRangeAction<TKind, TState>(
    TState state,
    int partition,
    scoped ref PopulationAccessScope<TKind> scope)
    where TKind : IPopulationKind;

/// <summary>Provides only the population rows authorized for one synchronous callback.</summary>
/// <remarks>
/// This is a ref struct passed as a scoped reference. Spans obtained from it cannot be returned from the callback,
/// stored in heap state, or used after the callback has completed.
/// </remarks>
public readonly ref struct PopulationAccessScope<TKind> where TKind : IPopulationKind
{
    private readonly PopulationLease<TKind>? _lease;
    private readonly Population<TKind>? _population;
    private readonly PopulationScopeLifetime _lifetime;
    private readonly long _generation;

    internal PopulationAccessScope(
        PopulationLease<TKind> lease,
        PopulationScopeLifetime lifetime,
        long generation)
    {
        _lease = lease;
        _population = null;
        _lifetime = lifetime;
        _generation = generation;
    }

    internal PopulationAccessScope(
        Population<TKind> population,
        PopulationScopeLifetime lifetime,
        long generation)
    {
        _lease = null;
        _population = population;
        _lifetime = lifetime;
        _generation = generation;
    }

    /// <summary>Gets the exact rows assigned to this callback.</summary>
    public PopulationRange Range
    {
        get
        {
            Validate();
            return _lifetime.Range;
        }
    }

    /// <summary>Captures a non-data token that validates whether this scope is still active.</summary>
    /// <remarks>The token may be retained safely because it exposes no population storage.</remarks>
    public PopulationAccessGuard Guard
    {
        get
        {
            Validate();
            return new PopulationAccessGuard(_lifetime, _generation);
        }
    }

    /// <summary>Reads every live row of a declared single-buffered column.</summary>
    public ReadOnlySpan<T> ReadAll<T>(PopulationColumn<T> column) where T : unmanaged
    {
        Validate();
        ValidateColumn(column);
        _lifetime.RequireColumn<TKind>(column, write: false, wholePopulation: true);
        return column.ReadOnlySpan;
    }

    /// <summary>Reads only the rows assigned to this callback.</summary>
    public ReadOnlySpan<T> ReadRange<T>(PopulationColumn<T> column) where T : unmanaged
    {
        Validate();
        ValidateColumn(column);
        _lifetime.RequireColumn<TKind>(column, write: false, wholePopulation: false);
        return column.ReadOnlySpan.Slice(_lifetime.Range.Start, _lifetime.Range.Length);
    }

    /// <summary>Writes only the rows assigned to this callback.</summary>
    public Span<T> EditRange<T>(PopulationColumn<T> column) where T : unmanaged
    {
        Validate();
        ValidateColumn(column);
        _lifetime.RequireColumn<TKind>(column, write: true, wholePopulation: false);
        return column.EditRange(_lifetime.Range.Start, _lifetime.Range.Length);
    }

    /// <summary>Reads every current row of a declared double-buffered column.</summary>
    public ReadOnlySpan<T> ReadCurrent<T>(DoubleBufferedPopulationColumn<T> column) where T : unmanaged
    {
        Validate();
        ValidateColumn(column);
        _lifetime.RequireColumn<TKind>(column, write: false, wholePopulation: true, currentBuffer: true);
        return column.Current;
    }

    /// <summary>Reads only assigned rows of the current double buffer.</summary>
    public ReadOnlySpan<T> ReadCurrentRange<T>(DoubleBufferedPopulationColumn<T> column) where T : unmanaged
    {
        Validate();
        ValidateColumn(column);
        _lifetime.RequireColumn<TKind>(column, write: false, wholePopulation: false, currentBuffer: true);
        return column.Current.Slice(_lifetime.Range.Start, _lifetime.Range.Length);
    }

    /// <summary>Writes only assigned rows of the next double buffer.</summary>
    public Span<T> EditNext<T>(DoubleBufferedPopulationColumn<T> column) where T : unmanaged
    {
        Validate();
        ValidateColumn(column);
        _lifetime.RequireColumn<TKind>(column, write: true, wholePopulation: false);
        return column.EditNext(_lifetime.Range.Start, _lifetime.Range.Length);
    }

    /// <summary>Reads all rows from another population declared read-only by this system.</summary>
    public ReadOnlySpan<T> ReadAll<TOtherKind, T>(
        PopulationLease<TOtherKind> lease,
        PopulationColumn<T> column)
        where TOtherKind : IPopulationKind
        where T : unmanaged
    {
        Validate();
        ArgumentNullException.ThrowIfNull(lease);
        if (!ReferenceEquals(_lifetime.StorageIdentity.World, lease.WorldIdentity))
        {
            throw new ArgumentException("Read-only population leases used in one callback must belong to the same world.", nameof(lease));
        }

        lease.ValidateScopedColumn(column);
        if (ReferenceEquals(_lifetime.StorageIdentity, lease.StorageIdentity))
        {
            _lifetime.RequireColumn<TKind>(column, write: false, wholePopulation: true);
            return column.ReadOnlySpan;
        }

        _lifetime.RequireReadOnlyColumn<TOtherKind>(column);
        lease.RegisterScopedDependency(_lifetime);
        return column.ReadOnlySpan;
    }

    /// <summary>Reads every current row from another declared population.</summary>
    public ReadOnlySpan<T> ReadCurrent<TOtherKind, T>(
        PopulationLease<TOtherKind> lease,
        DoubleBufferedPopulationColumn<T> column)
        where TOtherKind : IPopulationKind
        where T : unmanaged
    {
        Validate();
        ArgumentNullException.ThrowIfNull(lease);
        if (!ReferenceEquals(_lifetime.StorageIdentity.World, lease.WorldIdentity))
        {
            throw new ArgumentException("Read-only population leases used in one callback must belong to the same world.", nameof(lease));
        }

        lease.ValidateScopedColumn(column);
        if (ReferenceEquals(_lifetime.StorageIdentity, lease.StorageIdentity))
        {
            _lifetime.RequireColumn<TKind>(column, write: false, wholePopulation: true, currentBuffer: true);
            return column.Current;
        }

        _lifetime.RequireReadOnlyColumn<TOtherKind>(column);
        lease.RegisterScopedDependency(_lifetime);
        return column.Current;
    }

    /// <summary>Writes both buffers in the assigned range during serial initialization.</summary>
    public DoubleBufferedPopulationEdit<T> EditBoth<T>(DoubleBufferedPopulationColumn<T> column) where T : unmanaged
    {
        Validate();
        ValidateColumn(column);
        if (_lifetime.IsParallel)
        {
            throw new InvalidOperationException("Parallel partition scopes may only write the next buffer.");
        }

        _lifetime.RequireColumn<TKind>(column, write: true, wholePopulation: false);
        return column.EditBothBuffers(_lifetime.Range.Start, _lifetime.Range.Length);
    }

    private readonly void Validate()
    {
        if (_lifetime is null)
        {
            throw new InvalidOperationException("The population access scope is uninitialized.");
        }

        _lifetime.Validate(_generation);
    }

    private readonly void ValidateColumn(PopulationColumnBase column)
    {
        if (_lease is not null)
        {
            _lease.ValidateScopedColumn(column);
        }
        else
        {
            _population!.ValidateScopedColumn(column);
        }
    }
}

/// <summary>Validates the lifetime and thread of a callback-scoped population access grant.</summary>
public readonly struct PopulationAccessGuard
{
    private readonly PopulationScopeLifetime? _lifetime;
    private readonly long _generation;

    internal PopulationAccessGuard(PopulationScopeLifetime lifetime, long generation)
    {
        _lifetime = lifetime;
        _generation = generation;
    }

    /// <summary>Throws if the callback has ended or the caller is not on its assigned thread.</summary>
    public void Validate()
    {
        if (_lifetime is null)
        {
            throw new InvalidOperationException("The population access guard is uninitialized.");
        }

        _lifetime.Validate(_generation);
    }
}

internal sealed class PopulationScopeLifetime
{
    private PopulationStorage? _storage;
    private ImmutableArray<SystemAccess>? _access;
    private PopulationStorage[] _dependencies = new PopulationStorage[2];
    private int _dependencyCount;
    private object? _group;
    private int _active;
    private int _threadId;
    private long _generation;

    public PopulationRange Range { get; private set; }

    public bool IsParallel { get; private set; }

    public long Activate(PopulationStorage storage, PopulationRange range,
        ImmutableArray<SystemAccess>? access, bool parallel, object? group)
    {
        _storage = storage;
        Range = range;
        _access = access;
        IsParallel = parallel;
        _group = group;
        _dependencyCount = 0;
        _threadId = Environment.CurrentManagedThreadId;
        var generation = Interlocked.Increment(ref _generation);
        Volatile.Write(ref _active, 1);
        return generation;
    }

    public PopulationStorage StorageIdentity => _storage
        ?? throw new InvalidOperationException("The population access scope has expired.");

    public void Deactivate(long generation)
    {
        if (generation == Volatile.Read(ref _generation))
        {
            for (var index = _dependencyCount - 1; index >= 0; index--)
            {
                var dependency = _dependencies[index];
                lock (dependency.World.TransactionGate)
                {
                    dependency.ExitScope(IsParallel, _group);
                }

                _dependencies[index] = null!;
            }

            _dependencyCount = 0;
            Volatile.Write(ref _active, 0);
            _storage = null;
            _access = null;
            _group = null;
            _threadId = 0;
            Range = default;
            IsParallel = false;
        }
    }

    public void RegisterDependency(PopulationStorage storage)
    {
        if (ReferenceEquals(_storage, storage))
        {
            return;
        }

        for (var index = 0; index < _dependencyCount; index++)
        {
            if (ReferenceEquals(_dependencies[index], storage))
            {
                return;
            }
        }

        lock (storage.World.TransactionGate)
        {
            storage.EnterScope(IsParallel, _group, _access);
            try
            {
                if (_dependencyCount == _dependencies.Length)
                {
                    Array.Resize(ref _dependencies, checked(_dependencies.Length * 2));
                }

                _dependencies[_dependencyCount++] = storage;
            }
            catch
            {
                storage.ExitScope(IsParallel, _group);
                throw;
            }
        }
    }

    public void Validate(long generation)
    {
        if (Volatile.Read(ref _active) == 0 || generation != Volatile.Read(ref _generation)
            || _storage is null)
        {
            throw new InvalidOperationException("The population access scope has expired.");
        }

        if (Environment.CurrentManagedThreadId != _threadId)
        {
            throw new InvalidOperationException("A population access scope can only be used by its assigned callback thread.");
        }
    }

    public void RequireColumn<TKind>(PopulationColumnBase column, bool write, bool wholePopulation, bool currentBuffer = false)
        where TKind : IPopulationKind
    {
        if (_access is null)
        {
            return;
        }

        var (read, declaredWrite) = FindAccess<TKind>(column.Id);
        if (write)
        {
            if (!declaredWrite)
            {
                throw Undeclared(column, "write");
            }

            return;
        }

        if (IsParallel && declaredWrite && wholePopulation && !currentBuffer)
        {
            throw new InvalidOperationException($"Parallel population scopes cannot read the full single-buffered column '{column.Id}' when it is writable; use ReadRange for the assigned rows.");
        }

        if (read || (currentBuffer && declaredWrite))
        {
            return;
        }

        if (declaredWrite && !wholePopulation)
        {
            return;
        }

        throw Undeclared(column, "read");
    }

    public void RequireReadOnlyColumn<TKind>(PopulationColumnBase column)
        where TKind : IPopulationKind
    {
        if (_access is null)
        {
            return;
        }

        var (read, _) = FindAccess<TKind>(column.Id);
        if (!read)
        {
            throw Undeclared(column, "read from another population");
        }
    }

    private (bool Read, bool Write) FindAccess<TKind>(string columnId)
        where TKind : IPopulationKind
    {
        var read = false;
        var write = false;
        foreach (var item in _access!)
        {
            var resource = item.Resource.AsSpan();
            const string prefix = "population:";
            const string separator = "/column:";
            if (!resource.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            resource = resource[prefix.Length..];
            if (!resource.StartsWith(TKind.Id.AsSpan(), StringComparison.Ordinal))
            {
                continue;
            }

            resource = resource[TKind.Id.Length..];
            if (!resource.StartsWith(separator, StringComparison.Ordinal))
            {
                continue;
            }

            resource = resource[separator.Length..];
            if (!resource.SequenceEqual(columnId.AsSpan()))
            {
                continue;
            }

            read |= item.Mode == SystemAccessMode.Read;
            write |= item.Mode == SystemAccessMode.Write;
        }

        return (read, write);
    }

    private static InvalidOperationException Undeclared(PopulationColumnBase column, string operation) =>
        new($"The system did not declare {operation} access to population column '{column.Id}'.");
}
