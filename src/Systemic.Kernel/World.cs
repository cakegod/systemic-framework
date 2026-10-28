namespace Systemic.Kernel;

public enum ModifierOperation
{
    Add,
    Multiply,
    Set,
}

public enum ModifierStage
{
    Standard,
    Foundation,
    Additive,
    Multiplicative,
    Override,
    Final,
}

public sealed record PropertyModifier(
    string Source,
    int EntityId,
    string Property,
    ModifierOperation Operation,
    double Value,
    int ExpiryTick = -1,
    int Priority = 0,
    ModifierStage Stage = ModifierStage.Standard)
{
    /// <summary>Whether this modifier is permanent or still applies at the supplied tick.</summary>
    public bool IsActiveAt(int tick) => ExpiryTick < 0 || ExpiryTick >= tick;
}

public enum GrantKind
{
    Tag,
    Capability,
}

public sealed record PropertyGrant(
    string Source,
    int EntityId,
    GrantKind Kind,
    string Name,
    int ExpiryTick = -1)
{
    /// <summary>Whether this grant is permanent or still applies at the supplied tick.</summary>
    public bool IsActiveAt(int tick) => ExpiryTick < 0 || ExpiryTick >= tick;
}

/// <summary>Reports failures in callbacks invoked after a world transaction has committed.</summary>
/// <remarks>The transaction's world changes are already committed when this exception is thrown.</remarks>
public sealed class WorldCommitCallbackException(IEnumerable<Exception> failures)
    : AggregateException("World transaction commit callbacks failed after the state was committed.", failures);

public sealed class World
{
    public const float DefaultSpatialCellSize = 32.0f;

    [ThreadStatic]
    private static World? s_currentSystemWorld;

    [ThreadStatic]
    private static SystemAccess[]? s_currentSystemAccess;

    [ThreadStatic]
    private static DataPropertyMutationCacheEntry s_dataPropertyMutationCache;

    private int _logicalTick;
    private bool _modifierTickInProgress;
    private readonly Dictionary<(int EntityId, string Property), List<PropertyModifier>> _modifiersByKey = [];
    private readonly Dictionary<(int EntityId, string Property), long> _modifierVersions = [];
    private readonly Dictionary<(int EntityId, string Property), CachedModifierApplication> _modifierCache = [];
    private readonly Lock _effectivePropertyReadLock = new();
    private readonly Dictionary<int, List<PropertyGrant>> _grantsByEntity = [];

    private static readonly Dictionary<string, int> RoleHandles = [];
    private static readonly List<string> RoleNames = ["<unbound>"];
    private static int _nextRoleHandle = 1;
    private static readonly Lock s_adoptionGate = new();

    private readonly List<Entity> _entities = [];
    private readonly Dictionary<int, Bounds2> _spatialBoundsByEntity = [];
    private readonly SpatialIndex _spatialIndex;
    private Dictionary<int, Entity> _byId = [];
    private Dictionary<int, int> _entityPositions = [];
    private SortedSet<int> _removalCandidates = [];
    private Dictionary<int, HashSet<(int SourceId, string Link)>> _incomingLinks = [];
    private Dictionary<Type, object> _entityColumns = [];
    private Dictionary<Type, PopulationRegistration> _populationsByKind = [];
    private Dictionary<string, PopulationRegistration> _populationsById = new(StringComparer.Ordinal);
    private int[] _roles = new int[32];
    private readonly HashSet<int> _required = [];
    private int _nextId;
    private bool _consumed;

    // Serializes scope state, the undo journal, and column detachment/snapshot preparation.
    // Borrowed views are used outside this gate; callers must join workers before changing
    // scopes, cloning, swapping buffers, or making structural changes.
    internal Lock TransactionGate { get; } = new();
    private int _activePopulationLeases;
    private int _activeScheduledSystems;
    private readonly List<UndoRecord> _journal = [];
    private readonly Stack<ScopeState> _scopes = [];
    private readonly Stack<ScopeState> _freeScopes = [];
    private readonly List<(int EntityId, string Property)> _expiringModifierKeys = [];
    private readonly List<int> _expiringGrantEntities = [];
    private bool _undoing;
    private long _fullEntitySnapshotCount;
    private readonly Dictionary<Type, long> _fullEntitySnapshotCountsByType = [];

    private bool _populationDirty = true;
    private readonly Dictionary<string, int> _liveCounts = [];
    private readonly Dictionary<(string Tag, int EntityId), int> _liveRanks = [];
    private readonly HashSet<string> _indexedTags = [];
    private readonly Dictionary<(string Tag, string Capability), int> _firstTagged = [];
    private readonly Dictionary<string, CandidateBucket> _entityIdsByTag = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CandidateBucket> _entityIdsByCapability = new(StringComparer.Ordinal);
    private CandidateBucket _allEntityIds = new();
    private readonly Dictionary<Type, CandidateBucket> _entityIdsByType = [];
    internal long StructuralVersion { get; private set; }
    internal int UndoRecordCount
    {
        get { lock (TransactionGate) return _journal.Count; }
    }
    internal bool HasPendingRemovals => _removalCandidates.Count != 0;

    internal void MarkStructureChanged() => StructuralVersion++;

    // Callers hold TransactionGate while changing or checking these counts.
    internal void AcquirePopulationLease()
    {
        if (_activePopulationLeases == int.MaxValue)
            throw new InvalidOperationException("Too many active population leases.");
        _activePopulationLeases++;
    }

    internal void ReleasePopulationLease() => _activePopulationLeases--;

    internal void EnterScheduledSystem(
        SystemAccess[] access,
        out World? previousWorld,
        out SystemAccess[]? previousAccess)
    {
        ArgumentNullException.ThrowIfNull(access);
        lock (TransactionGate)
        {
            if (_activeScheduledSystems == int.MaxValue)
                throw new InvalidOperationException("Too many scheduled systems are active for this World.");
            _activeScheduledSystems++;
        }

        previousWorld = s_currentSystemWorld;
        previousAccess = s_currentSystemAccess;
        s_currentSystemWorld = this;
        s_currentSystemAccess = access;
    }

    internal void ExitScheduledSystem(World? previousWorld, SystemAccess[]? previousAccess)
    {
        if (!ReferenceEquals(s_currentSystemWorld, this))
            throw new InvalidOperationException("Scheduled system access must end on the thread that started it.");

        s_currentSystemWorld = previousWorld;
        s_currentSystemAccess = previousAccess;
        lock (TransactionGate)
            _activeScheduledSystems--;
    }

    internal SystemAccess[]? GetCurrentSystemAccess()
    {
        if (ReferenceEquals(s_currentSystemWorld, this))
            return s_currentSystemAccess ?? [];

        if (s_currentSystemWorld is not null)
            throw new InvalidOperationException("A scheduled system cannot use a population from another World.");

        if (Volatile.Read(ref _activeScheduledSystems) != 0)
            throw new InvalidOperationException("Population access from a scheduled system must use its execution thread and scoped context.");

        return null;
    }

    private void EnsureNoPopulationLeases()
    {
        if (_activePopulationLeases != 0)
            throw new InvalidOperationException("Complete all population leases before changing world or transaction structure.");
    }

    internal bool HasActiveScope
    {
        get { lock (TransactionGate) return !_undoing && _scopes.Count > 0; }
    }

    private bool _tagIndexDirty = true;

    public World(int seed, double valueScale, float spatialCellSize = DefaultSpatialCellSize)
    {
        if (!double.IsFinite(valueScale) || valueScale <= 0.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(valueScale), valueScale, "Value scale must be finite and positive.");
        }

        Seed = seed;
        ValueScale = valueScale;
        Draws = new DrawStream(seed);
        SpatialCellSize = spatialCellSize;
        _spatialIndex = new SpatialIndex(spatialCellSize);
    }

    private World(int seed, double valueScale, DrawStream draws, int capacity = 0,
        float spatialCellSize = DefaultSpatialCellSize)
    {
        Seed = seed;
        ValueScale = valueScale;
        Draws = draws;
        SpatialCellSize = spatialCellSize;
        _spatialIndex = new SpatialIndex(spatialCellSize);

        if (capacity > 0)
        {
            _entities = new List<Entity>(capacity);
            _byId = new Dictionary<int, Entity>(capacity);
        }
    }

    /// <summary>The original seed identifying this world and all of its cloned branches.</summary>
    public int Seed { get; }
    public double ValueScale { get; }
    public float SpatialCellSize { get; }
    public DrawStream Draws { get; private set; }
    /// <summary>Gets or sets the current logical tick.</summary>
    /// <remarks>Assigning a new tick marks that tick as in progress until modifiers advance.</remarks>
    public int LogicalTick
    {
        get => _logicalTick;
        set
        {
            if (_logicalTick == value)
            {
                return;
            }

            _logicalTick = value;
            _modifierTickInProgress = true;
        }
    }

    public int NextEntityId => checked(_nextId + 1);
    public IReadOnlyList<int> RequiredEntityIds => [.. _required.Order()];
    public IReadOnlyDictionary<string, int> BoundRoles
    {
        get
        {
            var roles = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var role = 1; role < _roles.Length; role++)
            {
                if (_roles[role] != 0)
                {
                    roles.Add(RoleNames[role], _roles[role]);
                }
            }

            return roles;
        }
    }

    internal long FullEntitySnapshotCount => _fullEntitySnapshotCount;

    internal IReadOnlyDictionary<Type, long> FullEntitySnapshotCountsByType =>
        _fullEntitySnapshotCountsByType;

    internal EntityColumn<TEntity, TState> GetEntityColumn<TEntity, TState>()
        where TEntity : Entity
        where TState : struct
    {
        var type = typeof(EntityColumn<TEntity, TState>);
        if (!_entityColumns.TryGetValue(type, out var existing))
        {
            existing = new EntityColumn<TEntity, TState>();
            _entityColumns.Add(type, existing);
        }

        return (EntityColumn<TEntity, TState>)existing;
    }

    internal ColumnMutationBatch<TEntity, TState> BeginColumnMutationBatch<TEntity, TState>()
        where TEntity : Entity
        where TState : struct
    {
        var column = GetEntityColumn<TEntity, TState>();
        EntityColumn<TEntity, TState>.ColumnUndoBatch? undoBatch = null;
        lock (TransactionGate)
        {
            if (!_undoing && _scopes.Count > 0)
            {
                undoBatch = column.RentUndoBatch();
                _journal.Add(UndoRecord.ForColumnUndo(undoBatch));
            }
        }

        return new ColumnMutationBatch<TEntity, TState>(this, column, undoBatch);
    }

    // WHY: rule code mutates entities in place; scalars and array access journal themselves, so Edit is only an explicit hint.
    public T Edit<T>(T entity)
        where T : Entity
    {
        if (!entity.SupportsCompactPropertyUndo || !ReferenceEquals(entity.OwnerWorld, this))
        {
            MarkMutated(entity);
        }
        else
        {
            lock (TransactionGate)
            {
                if (!_undoing && _scopes.Count > 0 && _journal.Count == _scopes.Peek().Mark)
                {
                    _journal.Add(UndoRecord.ForEditMarker());
                }
            }
        }

        return entity;
    }

    internal void MarkMutated(Entity entity)
    {
        lock (TransactionGate)
        {
            if (_undoing || _scopes.Count == 0)
            {
                return;
            }

            var scope = _scopes.Peek();
            if (scope.Touch(entity))
            {
                var snapshot = entity.Clone();
                _fullEntitySnapshotCount++;
                var type = entity.GetType();
                _fullEntitySnapshotCountsByType[type] = _fullEntitySnapshotCountsByType.GetValueOrDefault(type) + 1;
                _journal.Add(UndoRecord.ForEntitySnapshot(entity, snapshot));
            }
        }
    }

    internal void RecordPropertyMutation(
        Entity entity,
        string propertyName,
        EntityPropertyUndo previousValue)
    {
        lock (TransactionGate)
        {
            if (_undoing || _scopes.Count == 0)
            {
                return;
            }

            var scope = _scopes.Peek();
            if (scope.Touched?.Contains(entity) == true
                || !scope.CaptureProperty(entity.Id, propertyName))
            {
                return;
            }

            _journal.Add(UndoRecord.ForProperty(entity, propertyName, previousValue));
        }
    }

    internal void RecordDataEntityPropertyMutation(DataEntity entity, string propertyName, double previousValue)
    {
        var cached = s_dataPropertyMutationCache;
        if (!_undoing && _scopes.TryPeek(out var activeScope) && !activeScope.Closed)
        {
            var generation = activeScope.Generation;
            if (ReferenceEquals(cached.Scope, activeScope)
                && cached.Generation == generation
                && cached.EntityId == entity.Id
                && ReferenceEquals(cached.Property, propertyName))
            {
                return;
            }
        }

        lock (TransactionGate)
        {
            if (_undoing || _scopes.Count == 0)
            {
                return;
            }

            var scope = _scopes.Peek();
            if (scope.Touched?.Contains(entity) != true
                && scope.CaptureProperty(entity.Id, propertyName))
            {
                _journal.Add(UndoRecord.ForDataProperty(entity, propertyName, previousValue));
            }

            s_dataPropertyMutationCache = new DataPropertyMutationCacheEntry(
                scope, scope.Generation, entity.Id, propertyName);
        }
    }

    private readonly record struct DataPropertyMutationCacheEntry(
        ScopeState? Scope,
        ulong Generation,
        int EntityId,
        string? Property);

    public void MarkPopulationDirty() => _populationDirty = true;

    internal void RecordPopulationPage(
        PopulationColumnBase column,
        int bufferIndex,
        int pageIndex,
        int start,
        int length)
    {
        lock (TransactionGate)
        {
            if (_undoing || _scopes.Count == 0)
            {
                return;
            }

            var scope = _scopes.Peek();
            if (!scope.CapturePopulationPage(column, bufferIndex, pageIndex))
            {
                return;
            }

            IColumnUndoBatch? batch = null;
            try
            {
                batch = column.CapturePageUndo(bufferIndex, start, length);
                _journal.Add(UndoRecord.ForColumnUndo(batch));
                batch = null;
            }
            catch
            {
                try { batch?.Release(); } catch { /* Preserve the original failure. */ }
                scope.ReleasePopulationPageCapture(column, bufferIndex, pageIndex);
                throw;
            }
        }
    }

    internal bool RecordPopulationBuffer(
        PopulationColumnBase column,
        int bufferIndex,
        int capacity,
        int rowsPerPage)
    {
        lock (TransactionGate)
        {
            if (_undoing || _scopes.Count == 0)
            {
                return true;
            }

            var pageCount = capacity == 0 ? 0 : 1 + ((capacity - 1) / rowsPerPage);
            var scope = _scopes.Peek();
            if (!scope.CapturePopulationBuffer(column, bufferIndex, pageCount, out var alreadyCaptured))
            {
                return false;
            }

            if (alreadyCaptured)
            {
                return true;
            }

            IColumnUndoBatch? batch = null;
            try
            {
                batch = column.CapturePageUndo(bufferIndex, 0, capacity);
                _journal.Add(UndoRecord.ForColumnUndo(batch));
                batch = null;
                return true;
            }
            catch
            {
                try { batch?.Release(); } catch { /* Preserve the original failure. */ }
                scope.ReleasePopulationBufferCapture(column, bufferIndex);
                throw;
            }
        }
    }

    internal bool TryCapturePopulationStructure(object population)
    {
        lock (TransactionGate)
            return !_undoing && _scopes.Count > 0 && _scopes.Peek().CapturePopulationStructure(population);
    }

    internal void ReleasePopulationStructureCapture(object population)
    {
        lock (TransactionGate)
        {
            if (_scopes.Count > 0)
            {
                _scopes.Peek().ReleasePopulationStructureCapture(population);
            }
        }
    }

    internal bool TryCapturePopulationRole(PopulationColumnBase column)
    {
        lock (TransactionGate)
            return !_undoing && _scopes.Count > 0 && _scopes.Peek().CapturePopulationRole(column);
    }

    internal void ReleasePopulationRoleCapture(PopulationColumnBase column)
    {
        lock (TransactionGate)
        {
            if (_scopes.Count > 0)
            {
                _scopes.Peek().ReleasePopulationRoleCapture(column);
            }
        }
    }

    internal void RecordPopulationUndo(IColumnUndoBatch batch)
    {
        lock (TransactionGate)
        {
            if (_undoing || _scopes.Count == 0)
            {
                batch.Release();
                return;
            }

            _journal.Add(UndoRecord.ForColumnUndo(batch));
        }
    }

    internal WorldScope BeginScope()
    {
        lock (TransactionGate)
        {
            EnsureNoPopulationLeases();
            var scope = _freeScopes.Count > 0 ? _freeScopes.Pop() : new ScopeState();
            scope.Mark = _journal.Count;
            scope.Draws = Draws.Capture();
            scope.LogicalTick = LogicalTick;
            scope.NextId = _nextId;
            scope.Generation = unchecked(scope.Generation + 1);
            scope.Closed = false;
            scope.RollbackActions?.Clear();
            scope.CommitActions?.Clear();
            _scopes.Push(scope);

            return new WorldScope(this, scope, scope.Generation);
        }
    }

    /// <summary>Begins a public mutation scope for framework systems; disposal rolls back unless committed.</summary>
    /// <remarks>
    /// Population leases and scoped callbacks must end before changing scopes, cloning or adopting a world,
    /// swapping buffers, or changing population structure. The transaction journal does not make other World
    /// operations generally thread-safe.
    /// </remarks>
    public WorldTransaction BeginTransaction()
    {
        EnsureActive();
        return new WorldTransaction(BeginScope());
    }

    /// <summary>
    /// Registers external state restoration on the currently active transaction, if any. The
    /// callback runs when that scope or any enclosing scope rolls back. Returns false when there
    /// is no active transaction, so ordinary commands need not create a transaction just to track
    /// their own state.
    /// </summary>
    public bool TryRegisterRollbackAction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureActive();
        lock (TransactionGate)
        {
            if (_undoing || _scopes.Count == 0)
            {
                return false;
            }

            var scope = _scopes.Peek();
            (scope.RollbackActions ??= []).Add(action);
            return true;
        }
    }

    internal void RegisterRollbackAction(ScopeState scope, ulong generation, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (TransactionGate)
        {
            if (scope.Generation != generation || scope.Closed
                || _scopes.Count == 0 || !ReferenceEquals(_scopes.Peek(), scope))
            {
                throw new InvalidOperationException("Rollback actions must be registered on the active world transaction.");
            }

            (scope.RollbackActions ??= []).Add(action);
        }
    }

    internal void RegisterCommitAction(ScopeState scope, ulong generation, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (TransactionGate)
        {
            if (scope.Generation != generation || scope.Closed
                || _scopes.Count == 0 || !ReferenceEquals(_scopes.Peek(), scope))
            {
                throw new InvalidOperationException("Commit actions must be registered on the active world transaction.");
            }

            (scope.CommitActions ??= []).Add(action);
        }
    }

    internal bool HasChanges(ScopeState scope, ulong generation)
    {
        lock (TransactionGate)
            return scope.Generation == generation && !scope.Closed && _journal.Count > scope.Mark;
    }

    public IReadOnlyList<Entity> Entities => _entities;

    public bool HasPopulations
    {
        get
        {
            EnsureActive();
            return _populationsById.Count > 0;
        }
    }

    /// <summary>Captures registered populations in stable ID order using their column codecs.</summary>
    public PopulationWorldState CapturePopulationState()
    {
        EnsureActive();
        var populations = _populationsById.Values.OrderBy(population => population.Id, StringComparer.Ordinal)
            .Select(population => population.CaptureSaveState())
            .ToArray();
        return new PopulationWorldState(PopulationWorldState.CurrentVersion, populations);
    }

    /// <summary>Validates a complete population save before applying any population data.</summary>
    public void RestorePopulationState(PopulationWorldState state)
    {
        lock (TransactionGate)
        {
            EnsureActive();
            EnsureNoPopulationLeases();
            ArgumentNullException.ThrowIfNull(state);
            if (state.Version != PopulationWorldState.CurrentVersion || state.Populations is null)
            {
                throw new InvalidDataException("Population state has an unsupported schema version or missing populations.");
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var restorePlans = new List<Action>(state.Populations.Length);
            foreach (var populationState in state.Populations)
            {
                if (populationState is null || string.IsNullOrWhiteSpace(populationState.Id)
                    || !seen.Add(populationState.Id))
                {
                    throw new InvalidDataException("Population state contains an invalid or duplicate population ID.");
                }

                if (!_populationsById.TryGetValue(populationState.Id, out var population))
                {
                    throw new InvalidDataException($"Population state references unknown population '{populationState.Id}'.");
                }

                restorePlans.Add(population.PrepareRestore(populationState));
            }

            if (seen.Count != _populationsById.Count)
            {
                throw new InvalidDataException("Population state does not contain every registered population.");
            }

            foreach (var restore in restorePlans)
            {
                restore();
            }
        }
    }

    public Population<TKind> DefinePopulation<TKind>(bool enableHandles = false)
        where TKind : IPopulationKind
    {
        lock (TransactionGate)
        {
            EnsureActive();
            ArgumentException.ThrowIfNullOrWhiteSpace(TKind.Id);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TKind.Version);

            var kind = typeof(TKind);
            if (_populationsByKind.ContainsKey(kind) || _populationsById.ContainsKey(TKind.Id))
            {
                throw new InvalidOperationException($"Population '{TKind.Id}' is already defined in this world.");
            }

            var population = new Population<TKind>(this, enableHandles);
            var registration = new PopulationRegistration<TKind>(population);
            if (!_undoing && _scopes.Count > 0)
            {
                _journal.Add(UndoRecord.ForPopulationDefinition(registration));
            }

            _populationsByKind.Add(kind, registration);
            _populationsById.Add(TKind.Id, registration);
            return population;
        }
    }

    public Population<TKind> GetPopulation<TKind>()
        where TKind : IPopulationKind
    {
        EnsureActive();
        return _populationsByKind.TryGetValue(typeof(TKind), out var population)
            ? (Population<TKind>)population.Value
            : throw new KeyNotFoundException($"Population '{TKind.Id}' is not defined in this world.");
    }

    /// <summary>Looks up a population column by schema ID and column ID for row-addressed rules.</summary>
    internal bool TryGetPopulationColumn(string populationId, string columnId, out PopulationColumnBase column)
    {
        EnsureActive();
        if (!_populationsById.TryGetValue(populationId, out var registration))
        {
            column = null!;
            return false;
        }

        return registration.TryGetColumn(columnId, out column);
    }

    /// <summary>Resolves a population's schema tags for row-addressed rules.</summary>
    /// <summary>Resolves a population's row modifier slots for row-addressed rules.</summary>
    internal bool TryGetPopulationModifiers(string populationId, out PopulationModifierAccess modifiers)
    {
        EnsureActive();
        if (!_populationsById.TryGetValue(populationId, out var registration))
        {
            modifiers = default;
            return false;
        }

        modifiers = new PopulationModifierAccess(registration);
        return true;
    }

    internal bool TryGetPopulationTags(string populationId, out PopulationTagAccess tags)
    {
        EnsureActive();
        if (!_populationsById.TryGetValue(populationId, out var registration))
        {
            tags = default;
            return false;
        }

        tags = new PopulationTagAccess(registration);
        return true;
    }

    internal void EnsurePopulationActive() => EnsureActive();

    /// <summary>Number of live entities with spatial bounds.</summary>
    public int SpatialEntityCount => _spatialBoundsByEntity.Count;

    /// <summary>Set or move an entity's broadphase bounds. Spatial state participates in world scopes and cloning.</summary>
    public void SetSpatialBounds(Entity entity, Bounds2 bounds)
    {
        EnsureSpatialEntity(entity);
        if (_spatialBoundsByEntity.TryGetValue(entity.Id, out var previous) && previous == bounds)
        {
            return;
        }

        _ = _spatialIndex.ValidateUpsert(entity.Id, bounds);
        CaptureSpatialBounds(entity.Id);
        _spatialIndex.Upsert(entity.Id, bounds);
        _spatialBoundsByEntity[entity.Id] = bounds;
    }

    /// <summary>Checks that bounds fit the configured spatial index without changing world state.</summary>
    public void ValidateSpatialBounds(Bounds2 bounds) => _spatialIndex.ValidateBounds(bounds);

    public bool RemoveSpatialBounds(Entity entity)
    {
        EnsureSpatialEntity(entity);
        if (!_spatialBoundsByEntity.ContainsKey(entity.Id))
        {
            return false;
        }

        CaptureSpatialBounds(entity.Id);
        _spatialBoundsByEntity.Remove(entity.Id);
        _spatialIndex.Remove(entity.Id);
        return true;
    }

    public bool TryGetSpatialBounds(int entityId, out Bounds2 bounds) =>
        _spatialBoundsByEntity.TryGetValue(entityId, out bounds);

    public void QuerySpatialOverlaps(Bounds2 area, List<int> results)
    {
        _spatialIndex.QueryOverlaps(area, results);
        RemoveNonLiveSpatialEntities(results);
    }

    public void QuerySpatialNearby(Point2 point, float radius, List<int> results)
    {
        _spatialIndex.QueryNearby(point, radius, results);
        RemoveNonLiveSpatialEntities(results);
    }

    private void EnsureSpatialEntity(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        EnsureActive();
        if (!ReferenceEquals(entity.OwnerWorld, this) || !_byId.TryGetValue(entity.Id, out var attached)
            || !ReferenceEquals(attached, entity))
        {
            throw new InvalidOperationException("Spatial state requires an entity attached to this world.");
        }
    }

    private void CaptureSpatialBounds(int entityId)
    {
        lock (TransactionGate)
        {
            if (_undoing || _scopes.Count == 0)
            {
                return;
            }

            var hadBounds = _spatialBoundsByEntity.TryGetValue(entityId, out var bounds);
            _journal.Add(UndoRecord.ForSpatial(entityId, hadBounds, bounds));
        }
    }

    private void RestoreSpatialBounds(int entityId, bool hadBounds, Bounds2 bounds)
    {
        if (hadBounds && _byId.ContainsKey(entityId))
        {
            _spatialIndex.Upsert(entityId, bounds);
            _spatialBoundsByEntity[entityId] = bounds;
        }
        else
        {
            _spatialBoundsByEntity.Remove(entityId);
            _spatialIndex.Remove(entityId);
        }
    }

    private void RemoveNonLiveSpatialEntities(List<int> results)
    {
        if (_removalCandidates.Count == 0)
        {
            return;
        }

        var write = 0;
        for (var read = 0; read < results.Count; read++)
        {
            var id = results[read];
            if (ById(id) is { ToRemove: false })
            {
                results[write++] = id;
            }
        }

        if (write < results.Count)
        {
            results.RemoveRange(write, results.Count - write);
        }
    }

    // WHY: role handles let content resolve a role with an array index instead of string hashing on every access.
    public static int RegisterRole(string name)
    {
        if (RoleHandles.TryGetValue(name, out var handle))
        {
            return handle;
        }

        handle = _nextRoleHandle++;
        RoleHandles.Add(name, handle);
        RoleNames.Add(name);

        return handle;
    }

    public void BindRole(string role, Entity entity) => BindRole(RegisterRole(role), entity);

    public void BindRole(int role, Entity entity)
    {
        EnsureActive();

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(role);

        EnsureRoleCapacity(role);
        if (_roles[role] != 0)
        {
            throw new InvalidOperationException($"Role {RoleNames[role]} is already bound.");
        }

        CaptureRole(role);
        _roles[role] = entity.Id;
    }

    private void CaptureRole(int role)
    {
        lock (TransactionGate)
        {
            if (!_undoing && _scopes.Count > 0)
            {
                _journal.Add(UndoRecord.ForRole(role, _roles[role]));
            }
        }
    }

    public Entity? Role(string role) =>
        RoleHandles.TryGetValue(role, out var handle) ? Role(handle) : null;

    public Entity? Role(int role) =>
        role > 0 && role < _roles.Length && _roles[role] != 0 ? ById(_roles[role]) : null;

    public T Role<T>(string role)
        where T : Entity =>
        Role(role) as T
        ?? throw new InvalidOperationException($"Role {role} is not a {typeof(T).Name}.");

    public T Role<T>(int role)
        where T : Entity =>
        Role(role) as T
        ?? throw new InvalidOperationException(
            $"Role {RoleName(role)} is not a {typeof(T).Name}.");

    public IReadOnlyList<PropertyModifier> Modifiers => [.. _modifiersByKey
        .OrderBy(pair => pair.Key.EntityId)
        .ThenBy(pair => pair.Key.Property, StringComparer.Ordinal)
        .SelectMany(pair => pair.Value)];

    public IReadOnlyList<PropertyGrant> Grants => [.. _grantsByEntity
        .OrderBy(pair => pair.Key)
        .SelectMany(pair => pair.Value)];

    public int LiveCount(string tag)
    {
        EnsurePopulation(tag);

        return _liveCounts.GetValueOrDefault(tag, 0);
    }

    public int LiveRank(string tag, int entityId)
    {
        EnsurePopulation(tag);

        return _liveRanks.GetValueOrDefault((tag, entityId), -1);
    }

    public void Grant(
        string source,
        Entity entity,
        GrantKind kind,
        string name,
        int remainingTicks = -1)
    {
        var entityId = entity.Id;
        var hadMembership = HasMembership(entity, kind, name);
        CaptureGrants(entityId);
        if (!_grantsByEntity.TryGetValue(entityId, out var grants))
        {
            grants = [];
            _grantsByEntity.Add(entityId, grants);
        }

        grants.RemoveAll(grant => grant.Source == source && grant.Kind == kind && grant.Name == name);
        grants.Add(new PropertyGrant(source, entityId, kind, name, ExpiryFromRemaining(remainingTicks)));
        if (hadMembership != HasMembership(entity, kind, name))
        {
            UpdateMembershipIndex(entity, kind, name, present: true);
        }
    }

    public void Revoke(string source, Entity entity, GrantKind kind, string name)
    {
        var entityId = entity.Id;
        if (!_grantsByEntity.TryGetValue(entityId, out var grants)
            || !grants.Any(grant => grant.Source == source && grant.Kind == kind && grant.Name == name))
        {
            return;
        }

        var hadMembership = HasMembership(entity, kind, name);
        CaptureGrants(entityId);
        grants.RemoveAll(grant => grant.Source == source && grant.Kind == kind && grant.Name == name);
        if (hadMembership && !HasMembership(entity, kind, name))
        {
            UpdateMembershipIndex(entity, kind, name, present: false);
        }
    }

    public void RemoveEffectsBySource(string source)
    {
        foreach (var key in _modifiersByKey.Keys.ToArray())
        {
            var items = _modifiersByKey[key];
            if (!items.Any(modifier => modifier.Source == source))
            {
                continue;
            }

            CaptureModifiers(key);
            items.RemoveAll(modifier => modifier.Source == source);
            if (items.Count == 0)
            {
                _modifiersByKey.Remove(key);
            }

            InvalidateModifiers(key);
        }

        foreach (var (entityId, grants) in _grantsByEntity.ToArray())
        {
            var affectedMemberships = grants
                .Where(grant => grant.Source == source)
                .Select(grant => (grant.Kind, grant.Name))
                .Distinct()
                .ToArray();
            if (affectedMemberships.Length == 0)
            {
                continue;
            }

            CaptureGrants(entityId);
            grants.RemoveAll(grant => grant.Source == source);
            if (grants.Count == 0)
            {
                _grantsByEntity.Remove(entityId);
            }

            if (_byId.TryGetValue(entityId, out var entity))
            {
                foreach (var (kind, name) in affectedMemberships)
                {
                    if (!HasMembership(entity, kind, name))
                    {
                        UpdateMembershipIndex(entity, kind, name, present: false);
                    }
                }
            }
        }
    }

    private bool HasMembership(Entity entity, GrantKind kind, string name) =>
        kind == GrantKind.Tag ? HasTag(entity, name) : HasCapability(entity, name);

    public bool HasTag(Entity entity, string tag) =>
        entity.HasTag(tag) || HasGrant(entity.Id, GrantKind.Tag, tag);

    internal void MarkTagIndexDirty()
    {
        MarkStructureChanged();
        _tagIndexDirty = true;
        _firstTagged.Clear();
    }

    internal void MarkFirstTaggedDirty() => _firstTagged.Clear();

    internal void UpdateMembershipIndex(Entity entity, GrantKind kind, string name, bool present)
    {
        entity.MarkStructureChanged();
        MarkStructureChanged();
        MarkFirstTaggedDirty();
        if (_tagIndexDirty)
        {
            return;
        }

        var buckets = kind == GrantKind.Tag ? _entityIdsByTag : _entityIdsByCapability;
        if (present)
        {
            if (!buckets.TryGetValue(name, out var bucket))
            {
                bucket = new CandidateBucket();
                buckets.Add(name, bucket);
            }

            UpdateCandidateMembership(bucket, entity.Id, present: true);
        }
        else if (buckets.TryGetValue(name, out var bucket))
        {
            UpdateCandidateMembership(bucket, entity.Id, present: false);
        }
    }

    private void UpdateEntityIndex(Entity entity, bool present, bool includeGrants = false)
    {
        MarkStructureChanged();
        foreach (var (type, bucket) in _entityIdsByType)
        {
            if (type.IsInstanceOfType(entity))
            {
                UpdateCandidateMembership(bucket, entity.Id, present);
            }
        }

        MarkFirstTaggedDirty();
        if (_tagIndexDirty)
        {
            return;
        }

        UpdateCandidateMembership(_allEntityIds, entity.Id, present);
        foreach (var tag in entity.Tags)
        {
            UpdateMembershipIndex(entity, GrantKind.Tag, tag, present);
        }

        foreach (var capability in entity.Capabilities)
        {
            UpdateMembershipIndex(entity, GrantKind.Capability, capability, present);
        }

        if (includeGrants && _grantsByEntity.TryGetValue(entity.Id, out var grants))
        {
            foreach (var grant in grants)
            {
                UpdateMembershipIndex(entity, grant.Kind, grant.Name, present);
            }
        }
    }

    private static void UpdateCandidateMembership(CandidateBucket bucket, int entityId, bool present)
    {
        var changed = present
            ? bucket.EntityIds.Add(entityId)
            : bucket.EntityIds.Remove(entityId);
        if (changed)
        {
            bucket.SnapshotDirty = true;
        }
    }

    public bool HasCapability(Entity entity, string capability) =>
        entity.HasCapability(capability) || HasGrant(entity.Id, GrantKind.Capability, capability);

    private bool HasGrant(int entityId, GrantKind kind, string name)
    {
        if (!_grantsByEntity.TryGetValue(entityId, out var grants))
        {
            return false;
        }

        for (var index = 0; index < grants.Count; index++)
        {
            var grant = grants[index];
            if (grant.Kind == kind && grant.Name == name)
            {
                return true;
            }
        }

        return false;
    }


    public Entity? ById(int id) => _byId.GetValueOrDefault(id);

    public IEnumerable<TEntity> OfType<TEntity>() where TEntity : Entity =>
        ResolveTypeCandidates<TEntity>(TypeCandidates<TEntity>());

    internal ReadOnlyMemory<int> TypeCandidates<TEntity>() where TEntity : Entity
    {
        var type = typeof(TEntity);
        if (!_entityIdsByType.TryGetValue(type, out var candidates))
        {
            candidates = new CandidateBucket();
            foreach (var entity in _entities)
            {
                if (entity is TEntity)
                {
                    candidates.EntityIds.Add(entity.Id);
                }
            }

            _entityIdsByType.Add(type, candidates);
        }

        return CandidateSnapshot(candidates);
    }

    private IEnumerable<TEntity> ResolveTypeCandidates<TEntity>(ReadOnlyMemory<int> candidates)
        where TEntity : Entity
    {
        for (var index = 0; index < candidates.Length; index++)
        {
            if (ById(candidates.Span[index]) is TEntity entity)
            {
                yield return entity;
            }
        }
    }

    public int FirstTagged(string tag, string capability)
    {
        EnsureTagIndex();
        if (_firstTagged.TryGetValue((tag, capability), out var cached))
        {
            return cached;
        }

        var found = 0;
        var tagCandidates = _entityIdsByTag.GetValueOrDefault(tag);
        if (tagCandidates is null)
        {
            _firstTagged[(tag, capability)] = 0;
            return 0;
        }

        var candidates = tagCandidates;
        if (capability.Length > 0)
        {
            var capabilityCandidates = _entityIdsByCapability.GetValueOrDefault(capability);
            if (capabilityCandidates is null)
            {
                _firstTagged[(tag, capability)] = 0;
                return 0;
            }

            if (capabilityCandidates.EntityIds.Count < candidates.EntityIds.Count)
            {
                candidates = capabilityCandidates;
            }
        }

        foreach (var entityId in candidates.EntityIds)
        {
            if (ById(entityId) is { ToRemove: false } entity
                && HasTag(entity, tag)
                && (capability.Length == 0 || HasCapability(entity, capability)))
            {
                found = entity.Id;
                break;
            }
        }

        _firstTagged[(tag, capability)] = found;

        return found;
    }

    /// <summary>Returns the entity IDs that currently carry the tag.</summary>
    /// <param name="tag">Tag to enumerate.</param>
    /// <returns>The matching entity IDs in ascending order, or empty memory when none match.</returns>
    /// <remarks>
    /// This reads the tag index instead of scanning every entity, so per-tick work scales with the
    /// tagged population. The returned memory stays valid when the index is rebuilt during the call.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="tag"/> is null, empty or whitespace.</exception>
    public ReadOnlyMemory<int> EntitiesWithTag(string tag)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        EnsureTagIndex();

        return _entityIdsByTag.TryGetValue(tag, out var bucket)
            ? CandidateSnapshot(bucket)
            : ReadOnlyMemory<int>.Empty;
    }


    private void EnsureTagIndex()
    {
        if (!_tagIndexDirty)
        {
            return;
        }

        _entityIdsByTag.Clear();
        _entityIdsByCapability.Clear();
        _allEntityIds = new CandidateBucket();

        foreach (var entity in _entities)
        {
            var entityId = entity.Id;
            _allEntityIds.EntityIds.Add(entityId);

            foreach (var tag in entity.Tags)
            {
                AddCandidate(_entityIdsByTag, tag, entityId);
            }

            foreach (var capability in entity.Capabilities)
            {
                AddCandidate(_entityIdsByCapability, capability, entityId);
            }

            if (!_grantsByEntity.TryGetValue(entityId, out var grants))
            {
                continue;
            }

            HashSet<string>? grantedTags = null;
            HashSet<string>? grantedCapabilities = null;
            foreach (var grant in grants)
            {
                if (grant.Kind == GrantKind.Tag)
                {
                    if (!entity.HasTag(grant.Name)
                        && (grantedTags ??= new(StringComparer.Ordinal)).Add(grant.Name))
                    {
                        AddCandidate(_entityIdsByTag, grant.Name, entityId);
                    }
                }
                else if (!entity.HasCapability(grant.Name)
                    && (grantedCapabilities ??= new(StringComparer.Ordinal)).Add(grant.Name))
                {
                    AddCandidate(_entityIdsByCapability, grant.Name, entityId);
                }
            }
        }

        _tagIndexDirty = false;
    }

    private static void AddCandidate(Dictionary<string, CandidateBucket> buckets, string name, int entityId)
    {
        if (!buckets.TryGetValue(name, out var candidates))
        {
            candidates = new CandidateBucket();
            buckets.Add(name, candidates);
        }

        UpdateCandidateMembership(candidates, entityId, present: true);
    }

    private static ReadOnlyMemory<int> CandidateSnapshot(CandidateBucket bucket)
    {
        if (bucket.EntityIds.Count == 0)
        {
            return ReadOnlyMemory<int>.Empty;
        }

        if (bucket.SnapshotDirty)
        {
            bucket.Snapshot = [.. bucket.EntityIds];
            bucket.SnapshotDirty = false;
        }

        return bucket.Snapshot;
    }

    private sealed class CandidateBucket
    {
        public SortedSet<int> EntityIds { get; } = [];
        public int[] Snapshot { get; set; } = [];
        public bool SnapshotDirty { get; set; } = true;
    }

    public T Add<T>(T entity)
        where T : Entity
    {
        return AddRestored(entity, checked(_nextId + 1));
    }

    /// <summary>Attaches an entity with its saved ID so links and event references survive restoration.</summary>
    public T AddRestored<T>(T entity, int id)
        where T : Entity
    {
        EnsureActive();
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        EnsureCanAttach(entity);
        if (_byId.ContainsKey(id))
        {
            throw new InvalidOperationException($"Entity ID {id} is already attached to this world.");
        }

        lock (TransactionGate)
        {
            if (_scopes.Count > 0)
            {
                _journal.Add(UndoRecord.ForAdded(entity, entity.Id));
            }
        }
        entity.Id = id;
        _nextId = Math.Max(_nextId, id);
        Attach(entity);
        MarkPopulationDirty();
        UpdateEntityIndex(entity, present: true);

        return entity;
    }

    /// <summary>Restores the next free entity ID after loading a snapshot with removed-ID gaps.</summary>
    public void RestoreEntityIdSequence(int nextEntityId)
    {
        EnsureActive();
        if (nextEntityId < 1 || nextEntityId <= _nextId)
        {
            throw new ArgumentOutOfRangeException(nameof(nextEntityId), nextEntityId,
                "Next entity ID must be greater than every restored entity ID.");
        }

        _nextId = nextEntityId - 1;
    }

    public void MarkRequired(Entity entity)
    {
        lock (TransactionGate)
        {
            if (_required.Add(entity.Id) && !_undoing && _scopes.Count > 0)
            {
                _journal.Add(UndoRecord.ForRequired(entity.Id));
            }
        }
    }

    public void SetModifier(
        string source,
        Entity entity,
        string property,
        ModifierOperation operation,
        double value,
        int remainingTicks = -1,
        int priority = 0,
        ModifierStage stage = ModifierStage.Standard)
    {
        RemoveModifier(source, entity, property);

        var key = (entity.Id, property);
        CaptureModifiers(key);
        if (!_modifiersByKey.TryGetValue(key, out var items))
        {
            items = [];
            _modifiersByKey.Add(key, items);
        }

        items.Add(new PropertyModifier(
            source, entity.Id, property, operation, value, ExpiryFromRemaining(remainingTicks), priority, stage));
        items.Sort(CompareModifiers);
        InvalidateModifiers(key);
    }

    // WHY: durations are authored as a tick count, but stored as an absolute tick so the tick loop
    // never rewrites live state and never journals a decrement. A modifier created during a tick
    // counts that tick; one created between ticks counts from the next one.
    private int ExpiryFromRemaining(int remainingTicks)
    {
        if (remainingTicks < 0)
        {
            return -1;
        }

        var firstTick = _modifierTickInProgress ? LogicalTick : checked(LogicalTick + 1);

        return checked(firstTick + remainingTicks - 1);
    }

    /// <summary>Converts an authored duration into the absolute expiry tick row modifiers store.</summary>
    internal int ExpiryFromRemainingTicks(int remainingTicks) => ExpiryFromRemaining(remainingTicks);

    public void RemoveModifier(string source, Entity entity, string property)
    {
        var key = (entity.Id, property);
        if (!_modifiersByKey.TryGetValue(key, out var items)
            || !items.Any(modifier => modifier.Source == source))
        {
            return;
        }

        CaptureModifiers(key);
        items.RemoveAll(modifier => modifier.Source == source);
        if (items.Count == 0)
        {
            _modifiersByKey.Remove(key);
        }

        InvalidateModifiers(key);
    }

    public bool TryGetEffectiveProperty(Entity entity, string property, out double value)
    {
        if (!entity.TryGetProperty(this, property, out value))
        {
            return false;
        }

        value = ApplyModifiers(entity, property, value);

        return true;
    }

    internal bool TryReadEffectiveProperty(Entity entity, string property, out double value)
    {
        lock (_effectivePropertyReadLock)
        {
            return TryGetEffectiveProperty(entity, property, out value);
        }
    }

    public double EffectiveProperty(Entity entity, string property) =>
        TryGetEffectiveProperty(entity, property, out var value) ? value : 0.0;

    internal double EffectiveProperty(Entity entity, string property, double baseValue) =>
        ApplyModifiers(entity, property, baseValue);

    private double ApplyModifiers(Entity entity, string property, double value)
    {
        var key = (entity.Id, property);
        if (!_modifiersByKey.TryGetValue(key, out var items))
        {
            return value;
        }

        var version = _modifierVersions.GetValueOrDefault(key);
        if (_modifierCache.TryGetValue(key, out var cached)
            && cached.Version == version
            && BitConverter.DoubleToInt64Bits(cached.BaseValue) == BitConverter.DoubleToInt64Bits(value))
        {
            return cached.Effective;
        }

        var effective = value;
        foreach (var modifier in items)
        {
            effective = modifier.Operation switch
            {
                ModifierOperation.Add => effective + modifier.Value,
                ModifierOperation.Multiply => effective * modifier.Value,
                ModifierOperation.Set => modifier.Value,
                _ => effective,
            };
        }

        _modifierCache[key] = new CachedModifierApplication(value, version, effective);

        return effective;
    }

    private void InvalidateModifiers((int EntityId, string Property) key)
    {
        if (_modifiersByKey.ContainsKey(key))
        {
            _modifierVersions[key] = _modifierVersions.GetValueOrDefault(key) + 1;
        }
        else
        {
            _modifierVersions.Remove(key);
        }

        _modifierCache.Remove(key);
    }

    private void CaptureModifiers((int EntityId, string Property) key)
    {
        lock (TransactionGate)
        {
            if (_scopes.Count == 0)
            {
                return;
            }

            var scope = _scopes.Peek();
            if (!scope.CaptureModifier(key.EntityId, key.Property))
            {
                return;
            }

            _journal.Add(UndoRecord.ForModifier(key,
                _modifiersByKey.TryGetValue(key, out var items) ? [.. items] : null));
        }
    }

    private void CaptureGrants(int entityId)
    {
        lock (TransactionGate)
        {
            if (_scopes.Count == 0)
            {
                return;
            }

            var scope = _scopes.Peek();
            if (!scope.CaptureGrant(entityId))
            {
                return;
            }

            _journal.Add(UndoRecord.ForGrants(entityId,
                _grantsByEntity.TryGetValue(entityId, out var grants) ? [.. grants] : null));
        }
    }

    private void RestoreModifierKey((int EntityId, string Property) key, PropertyModifier[]? items)
    {
        if (items is null)
        {
            _modifiersByKey.Remove(key);
        }
        else
        {
            _modifiersByKey[key] = [.. items];
        }

        InvalidateModifiers(key);
    }

    private void RestoreGrants(int entityId, PropertyGrant[]? grants)
    {
        var currentGrants = _grantsByEntity.TryGetValue(entityId, out var current)
            ? current
            : [];
        var restoredGrants = grants ?? [];
        var entity = ById(entityId);
        var priorMemberships = entity is null
            ? []
            : currentGrants.Concat(restoredGrants)
                .Select(grant => (grant.Kind, grant.Name))
                .Distinct()
                .Select(key => (key.Kind, key.Name, WasPresent: HasMembership(entity, key.Kind, key.Name)))
                .ToArray();

        if (grants is null)
        {
            _grantsByEntity.Remove(entityId);
        }
        else
        {
            _grantsByEntity[entityId] = [.. grants];
        }

        if (entity is null)
        {
            return;
        }

        foreach (var (kind, name, wasPresent) in priorMemberships)
        {
            var isPresent = HasMembership(entity, kind, name);
            if (wasPresent != isPresent)
            {
                UpdateMembershipIndex(entity, kind, name, isPresent);
            }
        }
    }

    private static int CompareModifiers(PropertyModifier left, PropertyModifier right)
    {
        var byStage = left.Stage.CompareTo(right.Stage);
        if (byStage != 0)
        {
            return byStage;
        }

        var byPriority = left.Priority.CompareTo(right.Priority);

        return byPriority != 0
            ? byPriority
            : string.CompareOrdinal(left.Source, right.Source);
    }

    public void AdvanceModifiers()
    {
        _expiringModifierKeys.Clear();
        foreach (var (key, items) in _modifiersByKey)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index].ExpiryTick >= 0 && items[index].ExpiryTick <= LogicalTick)
                {
                    _expiringModifierKeys.Add(key);

                    break;
                }
            }
        }

        foreach (var key in _expiringModifierKeys)
        {
            if (!_modifiersByKey.TryGetValue(key, out var items))
            {
                continue;
            }

            CaptureModifiers(key);
            for (var index = items.Count - 1; index >= 0; index--)
            {
                var modifier = items[index];
                if (modifier.ExpiryTick >= 0 && modifier.ExpiryTick <= LogicalTick)
                {
                    items.RemoveAt(index);
                }
            }

            if (items.Count == 0)
            {
                _modifiersByKey.Remove(key);
            }

            InvalidateModifiers(key);
        }

        _modifierTickInProgress = false;
        _expiringGrantEntities.Clear();
        foreach (var (entityId, grants) in _grantsByEntity)
        {
            for (var index = 0; index < grants.Count; index++)
            {
                if (grants[index].ExpiryTick >= 0 && grants[index].ExpiryTick <= LogicalTick)
                {
                    _expiringGrantEntities.Add(entityId);

                    break;
                }
            }
        }

        foreach (var entityId in _expiringGrantEntities)
        {
            if (!_grantsByEntity.TryGetValue(entityId, out var grants))
            {
                continue;
            }

            CaptureGrants(entityId);
            HashSet<(GrantKind Kind, string Name)>? expiredMemberships = null;
            for (var index = grants.Count - 1; index >= 0; index--)
            {
                var grant = grants[index];
                if (grant.ExpiryTick >= 0 && grant.ExpiryTick <= LogicalTick)
                {
                    grants.RemoveAt(index);
                    (expiredMemberships ??= []).Add((grant.Kind, grant.Name));
                }
            }

            if (grants.Count == 0)
            {
                _grantsByEntity.Remove(entityId);
            }

            if (expiredMemberships is not null && _byId.TryGetValue(entityId, out var entity))
            {
                foreach (var (kind, name) in expiredMemberships)
                {
                    if (!HasMembership(entity, kind, name))
                    {
                        UpdateMembershipIndex(entity, kind, name, present: false);
                    }
                }
            }
        }
    }

    internal void UpdateRemovalCandidate(Entity entity)
    {
        MarkStructureChanged();
        if (entity.ToRemove)
        {
            _removalCandidates.Add(entity.Id);
        }
        else
        {
            _removalCandidates.Remove(entity.Id);
        }
    }

    internal void UpdateLinkIndex(Entity entity, string link, int? previousTarget, int? target)
    {
        if (previousTarget == target)
        {
            return;
        }

        entity.MarkStructureChanged();
        MarkStructureChanged();
        if (previousTarget is { } previous && _incomingLinks.TryGetValue(previous, out var previousLinks))
        {
            previousLinks.Remove((entity.Id, link));
            if (previousLinks.Count == 0)
            {
                _incomingLinks.Remove(previous);
            }
        }

        if (target is { } next)
        {
            if (!_incomingLinks.TryGetValue(next, out var nextLinks))
            {
                nextLinks = [];
                _incomingLinks.Add(next, nextLinks);
            }

            nextLinks.Add((entity.Id, link));
        }
    }

    private void AttachStructuralIndexes(Entity entity, int index)
    {
        for (var position = index; position < _entities.Count; position++)
        {
            _entityPositions[_entities[position].Id] = position;
        }

        UpdateRemovalCandidate(entity);
        foreach (var (link, target) in entity.Links)
        {
            UpdateLinkIndex(entity, link, null, target);
        }
    }

    private void DetachStructuralIndexes(Entity entity)
    {
        _entityPositions.Remove(entity.Id);
        _removalCandidates.Remove(entity.Id);
        _spatialBoundsByEntity.Remove(entity.Id);
        _spatialIndex.Remove(entity.Id);
        foreach (var (link, target) in entity.Links)
        {
            UpdateLinkIndex(entity, link, target, null);
        }
    }

    private void RemoveEntityAt(int index)
    {
        var entity = _entities[index];
        DetachStructuralIndexes(entity);
        _entities.RemoveAt(index);
        for (var position = index; position < _entities.Count; position++)
        {
            _entityPositions[_entities[position].Id] = position;
        }
    }

    public void PruneRemoved()
    {
        EnsureActive();

        if (_removalCandidates.Count == 0)
        {
            return;
        }

        var removed = new List<(Entity Entity, int Index)>(_removalCandidates.Count);
        foreach (var id in _removalCandidates)
        {
            var entity = _byId[id];
            if (IsRequired(entity))
            {
                throw new InvalidOperationException($"Cannot remove required entity {entity.Id}.");
            }

            removed.Add((entity, _entityPositions[id]));
        }

        removed.Sort(static (left, right) => left.Index.CompareTo(right.Index));

        foreach (var (entity, _) in removed)
        {
            UpdateEntityIndex(entity, present: false, includeGrants: true);
        }

        var removedIds = removed.Select(entry => entry.Entity.Id).ToHashSet();
        lock (TransactionGate)
        {
            if (_scopes.Count > 0)
            {
                // WHY: scopes undo in reverse, so journal highest indexes first to restore list order.
                for (var index = removed.Count - 1; index >= 0; index--)
                {
                    var removedEntity = removed[index].Entity;
                    var hasSpatialBounds = _spatialBoundsByEntity.TryGetValue(removedEntity.Id, out var bounds);
                    _journal.Add(UndoRecord.ForRemoved(removed[index].Index, removedEntity, hasSpatialBounds, bounds));
                }
            }
        }

        foreach (var key in _modifiersByKey.Keys.Where(key => removedIds.Contains(key.EntityId)).ToArray())
        {
            CaptureModifiers(key);
            _modifiersByKey.Remove(key);
            InvalidateModifiers(key);
        }

        foreach (var entityId in _grantsByEntity.Keys.Where(removedIds.Contains).ToArray())
        {
            CaptureGrants(entityId);
            _grantsByEntity.Remove(entityId);
        }

        var writePosition = removed[0].Index;
        var removalIndex = 0;
        for (var readPosition = writePosition; readPosition < _entities.Count; readPosition++)
        {
            var entity = _entities[readPosition];
            if (removalIndex < removed.Count && readPosition == removed[removalIndex].Index)
            {
                DetachStructuralIndexes(entity);
                removalIndex++;
            }
            else
            {
                _entities[writePosition] = entity;
                _entityPositions[entity.Id] = writePosition++;
            }
        }

        _entities.RemoveRange(writePosition, _entities.Count - writePosition);

        foreach (var id in removedIds)
        {
            if (_byId.Remove(id, out var entity))
            {
                if (entity is IWorldStateAttachment attachment)
                {
                    attachment.DetachFromWorld(this);
                }

                entity.OwnerWorld = null;
            }

            _required.Remove(id);
        }

        for (var role = 1; role < _roles.Length; role++)
        {
            if (removedIds.Contains(_roles[role]))
            {
                CaptureRole(role);
                _roles[role] = 0;
            }
        }

        var linkedSources = new SortedSet<int>();
        foreach (var id in removedIds)
        {
            if (_incomingLinks.TryGetValue(id, out var links))
            {
                foreach (var (sourceId, _) in links)
                {
                    linkedSources.Add(sourceId);
                }
            }
        }

        foreach (var sourceId in linkedSources)
        {
            var entity = _byId[sourceId];
            foreach (var link in entity.Links
                .Where(link => removedIds.Contains(link.Value))
                .Select(link => link.Key)
                .ToArray())
            {
                Edit(entity).RemoveLink(link);
            }
        }

        MarkPopulationDirty();
        MarkFirstTaggedDirty();
    }

    /// <summary>Creates an independent world branch, sharing immutable population buffers until a branch writes.</summary>
    /// <remarks>
    /// Active population leases and scoped callbacks prevent cloning. Population spans are exposed only inside
    /// synchronous callbacks, and every callback must finish before cloning can proceed.
    /// </remarks>
    public World Clone()
    {
        lock (TransactionGate)
        {
            EnsureActive();
            EnsureNoPopulationLeases();

            var clone = new World(Seed, ValueScale, Draws.Clone(), _entities.Count, SpatialCellSize)
            {
                LogicalTick = LogicalTick,
                _nextId = _nextId,
            };

            foreach (var entity in _entities)
            {
                clone.Attach(entity.Clone());
            }

            CopySpatialBoundsTo(clone);

            clone._roles = (int[])_roles.Clone();

            clone._required.UnionWith(_required);
            clone.ValidateRoles();
            CopyEffectsTo(clone);
            foreach (var (kind, registration) in _populationsByKind)
            {
                var clonedRegistration = registration.CloneTo(clone);
                clone._populationsByKind.Add(kind, clonedRegistration);
                clone._populationsById.Add(clonedRegistration.Id, clonedRegistration);
            }

            return clone;
        }
    }

    private void CopySpatialBoundsTo(World target)
    {
        foreach (var (entityId, bounds) in _spatialBoundsByEntity)
        {
            target._spatialBoundsByEntity[entityId] = bounds;
            target._spatialIndex.Upsert(entityId, bounds);
        }
    }

    internal void AdoptActionWorld(World next)
    {
        lock (s_adoptionGate)
            lock (TransactionGate)
                lock (next.TransactionGate)
                {
                    EnsureActive();
                    next.EnsureActive();
                    EnsureNoPopulationLeases();
                    next.EnsureNoPopulationLeases();

                    if (ReferenceEquals(this, next))
                    {
                        return;
                    }

                    if (Seed != next.Seed)
                    {
                        throw new InvalidOperationException("Cannot adopt a world created with a different seed.");
                    }

                    next.ValidateRoles();

                    foreach (var registration in _populationsByKind.Values)
                    {
                        registration.DetachWorld();
                    }

                    _populationsByKind = next._populationsByKind;
                    _populationsById = next._populationsById;
                    foreach (var registration in _populationsByKind.Values)
                    {
                        registration.RebindWorld(this);
                    }

                    next._populationsByKind = [];
                    next._populationsById = new Dictionary<string, PopulationRegistration>(StringComparer.Ordinal);

                    foreach (var entity in _entities)
                    {
                        if (entity is IWorldStateAttachment attachment)
                        {
                            attachment.DetachFromWorld(this);
                        }

                        entity.OwnerWorld = null;
                    }

                    var draws = next.Draws;

                    _entityIdsByType.Clear();
                    _entities.Clear();
                    _entities.AddRange(next._entities);
                    foreach (var entity in _entities)
                    {
                        entity.OwnerWorld = this;
                    }

                    _entityColumns = next._entityColumns;
                    next._entityColumns = [];

                    _spatialBoundsByEntity.Clear();
                    _spatialIndex.Clear();
                    next.CopySpatialBoundsTo(this);
                    next._spatialBoundsByEntity.Clear();
                    next._spatialIndex.Clear();

                    _byId = next._byId;
                    _entityPositions = next._entityPositions;
                    _removalCandidates = next._removalCandidates;
                    _incomingLinks = next._incomingLinks;
                    next._entityPositions = [];
                    next._removalCandidates = [];
                    next._incomingLinks = [];
                    _roles = next._roles;
                    next._roles = [];

                    _required.Clear();
                    _required.UnionWith(next._required);
                    Draws = draws;
                    LogicalTick = next.LogicalTick;
                    _nextId = next._nextId;
                    CopyEffectsTo(this, next);
                    MarkPopulationDirty();
                    MarkTagIndexDirty();

                    next.Draws = draws.Clone();
                    next._entities.Clear();
                    next._byId = [];
                    next._required.Clear();
                    next._modifiersByKey.Clear();
                    next._modifierVersions.Clear();
                    next._modifierCache.Clear();
                    next._grantsByEntity.Clear();
                    next._consumed = true;
                }
    }

    private void CopyEffectsTo(World target, World? source = null)
    {
        source ??= this;

        target._modifiersByKey.Clear();
        target._modifierVersions.Clear();
        target._modifierCache.Clear();
        target._grantsByEntity.Clear();

        foreach (var (key, items) in source._modifiersByKey)
        {
            target._modifiersByKey[key] = [.. items];
            target._modifierVersions[key] = 1;
        }

        foreach (var (entityId, grants) in source._grantsByEntity)
        {
            target._grantsByEntity[entityId] = [.. grants];
        }
    }

    private void Attach(Entity entity)
    {
        EnsureCanAttach(entity);

        _entities.Add(entity);
        _byId.Add(entity.Id, entity);
        AttachStructuralIndexes(entity, _entities.Count - 1);
        entity.OwnerWorld = this;
        if (entity is IWorldStateAttachment attachment)
        {
            attachment.AttachToWorld(this);
        }
    }

    private static void EnsureCanAttach(Entity entity)
    {
        if (entity.OwnerWorld is not null)
        {
            throw new InvalidOperationException("Entity already belongs to a world.");
        }
    }

    internal bool IsRequired(Entity entity) => _required.Contains(entity.Id);

    private void ValidateRoles()
    {
        for (var role = 1; role < _roles.Length; role++)
        {
            if (_roles[role] != 0 && ById(_roles[role]) is null)
            {
                throw new InvalidOperationException(
                    $"Role {RoleName(role)} points to a missing entity.");
            }
        }
    }

    private static string RoleName(int role) =>
        role > 0 && role < RoleNames.Count ? RoleNames[role] : role.ToString();

    private void EnsureRoleCapacity(int role)
    {
        if (role < _roles.Length)
        {
            return;
        }

        var grown = new int[Math.Max(role + 1, _roles.Length * 2)];
        Array.Copy(_roles, grown, _roles.Length);
        _roles = grown;
    }

    private void EnsureActive()
    {
        if (_consumed)
        {
            throw new InvalidOperationException("World state has been transferred.");
        }
    }

    private void EnsurePopulation(string tag)
    {
        if (_populationDirty)
        {
            _populationDirty = false;
            _liveCounts.Clear();
            _liveRanks.Clear();
            _indexedTags.Clear();
        }

        if (!_indexedTags.Add(tag))
        {
            return;
        }

        var live = 0;
        foreach (var id in EntitiesWithTag(tag).Span)
        {
            if (ById(id) is { ToRemove: false } entity && HasTag(entity, tag))
            {
                _liveRanks[(tag, entity.Id)] = live++;
            }
        }

        _liveCounts[tag] = live;
    }

    internal void EndScope(ScopeState scope, ulong generation, bool rollback)
    {
        List<Exception>? callbackErrors = null;
        List<Action>? callbacksToRun = null;
        lock (TransactionGate)
        {
            if (scope.Generation != generation || scope.Closed)
            {
                return;
            }

            if (_scopes.Count == 0 || !ReferenceEquals(_scopes.Peek(), scope))
            {
                throw new InvalidOperationException("World scopes must close in order.");
            }

            EnsureNoPopulationLeases();
            scope.Closed = true;
            _scopes.Pop();
            if (rollback)
            {
                _undoing = true;
                try
                {
                    for (var index = _journal.Count - 1; index >= scope.Mark; index--)
                    {
                        _journal[index].Undo(this);
                    }
                }
                finally
                {
                    _undoing = false;
                }

                var journalEnd = _journal.Count;
                for (var index = scope.Mark; index < journalEnd; index++)
                {
                    _journal[index].Release();
                }

                _journal.RemoveRange(scope.Mark, journalEnd - scope.Mark);

                Draws.Restore(scope.Draws);
                LogicalTick = scope.LogicalTick;
                _nextId = scope.NextId;

                callbacksToRun = scope.RollbackActions;
                scope.CommitActions?.Clear();
            }
            else if (_scopes.Count > 0)
            {
                var parent = _scopes.Peek();
                parent.InheritPopulationCaptures(scope);
                if (scope.RollbackActions is { Count: > 0 } rollbackActions)
                {
                    (parent.RollbackActions ??= []).AddRange(rollbackActions);
                }

                if (scope.CommitActions is { Count: > 0 } commitActions)
                {
                    (parent.CommitActions ??= []).AddRange(commitActions);
                }
            }
            else
            {
                callbacksToRun = scope.CommitActions;
            }

            if (rollback)
            {
                MarkStructureChanged();
                MarkPopulationDirty();
            }

            scope.CapturedProperties?.Clear();
            scope.Touched?.Clear();
            scope.CapturedModifierKeys?.Clear();
            scope.CapturedGrantEntities?.Clear();
            scope.CapturedPopulationPages?.Clear();
            scope.CapturedPopulationBuffers?.Clear();
            scope.CapturedPopulationStructures?.Clear();
            scope.CapturedPopulationRoles?.Clear();
            if (_scopes.Count == 0)
            {
                foreach (var record in _journal)
                {
                    record.Release();
                }

                _journal.Clear();
            }
        }

        // Callbacks can perform external work or enter a new transaction. Do not hold the
        // journal gate while invoking user code.
        if (callbacksToRun is not null)
        {
            if (rollback)
            {
                for (var index = callbacksToRun.Count - 1; index >= 0; index--)
                {
                    try
                    {
                        callbacksToRun[index]();
                    }
                    catch (Exception exception)
                    {
                        (callbackErrors ??= []).Add(exception);
                    }
                }
            }
            else
            {
                foreach (var callback in callbacksToRun)
                {
                    try
                    {
                        callback();
                    }
                    catch (Exception exception)
                    {
                        (callbackErrors ??= []).Add(exception);
                    }
                }
            }
        }

        lock (TransactionGate)
        {
            scope.RollbackActions?.Clear();
            scope.CommitActions?.Clear();
            _freeScopes.Push(scope);
        }
        if (callbackErrors is not null)
        {
            if (rollback)
            {
                throw new AggregateException("World transaction rollback callbacks failed.", callbackErrors);
            }

            throw new WorldCommitCallbackException(callbackErrors);
        }
    }

    internal sealed class ScopeState
    {
        public int Mark { get; set; }
        public DrawStreamSnapshot Draws { get; set; }
        public int LogicalTick { get; set; }
        public int NextId { get; set; }
        // ScopeState instances are pooled. Handles from a previous use must stay closed
        // even if this instance is already serving a different nested transaction.
        public ulong Generation { get; set; }
        public bool Closed { get; set; }
        public HashSet<Entity>? Touched { get; private set; }
        public HashSet<(int EntityId, string Property)>? CapturedProperties { get; private set; }
        public HashSet<(int EntityId, string Property)>? CapturedModifierKeys { get; private set; }
        public HashSet<int>? CapturedGrantEntities { get; private set; }
        public HashSet<PopulationPageKey>? CapturedPopulationPages { get; private set; }
        public HashSet<PopulationBufferKey>? CapturedPopulationBuffers { get; private set; }
        public HashSet<object>? CapturedPopulationStructures { get; private set; }
        public HashSet<PopulationColumnBase>? CapturedPopulationRoles { get; private set; }

        public List<Action>? RollbackActions { get; set; }
        public List<Action>? CommitActions { get; set; }

        public bool Touch(Entity entity) =>
            (Touched ??= new(ReferenceEqualityComparer.Instance)).Add(entity);

        public bool CaptureModifier(int entityId, string property) =>
            (CapturedModifierKeys ??= []).Add((entityId, property));

        public bool CaptureProperty(int entityId, string property) =>
            (CapturedProperties ??= []).Add((entityId, property));

        public bool CaptureGrant(int entityId) => (CapturedGrantEntities ??= []).Add(entityId);

        public bool CapturePopulationPage(PopulationColumnBase column, int bufferIndex, int pageIndex)
        {
            if (HasPopulationBuffer(column, bufferIndex))
            {
                return false;
            }

            return (CapturedPopulationPages ??= []).Add(new PopulationPageKey(column, bufferIndex, pageIndex));
        }

        public bool CapturePopulationBuffer(
            PopulationColumnBase column,
            int bufferIndex,
            int pageCount,
            out bool alreadyCaptured)
        {
            alreadyCaptured = HasPopulationBuffer(column, bufferIndex);
            if (alreadyCaptured)
            {
                return true;
            }

            for (var page = 0; page < pageCount; page++)
            {
                if (CapturedPopulationPages?.Contains(new PopulationPageKey(column, bufferIndex, page)) == true)
                {
                    return false;
                }
            }

            (CapturedPopulationBuffers ??= []).Add(new PopulationBufferKey(column, bufferIndex));
            return true;
        }

        public bool HasPopulationBuffer(PopulationColumnBase column, int bufferIndex) =>
            CapturedPopulationBuffers?.Contains(new PopulationBufferKey(column, bufferIndex)) == true;

        public void ReleasePopulationBufferCapture(PopulationColumnBase column, int bufferIndex) =>
            CapturedPopulationBuffers?.Remove(new PopulationBufferKey(column, bufferIndex));

        public void ReleasePopulationPageCapture(PopulationColumnBase column, int bufferIndex, int pageIndex) =>
            CapturedPopulationPages?.Remove(new PopulationPageKey(column, bufferIndex, pageIndex));

        public bool CapturePopulationStructure(object population) =>
            (CapturedPopulationStructures ??= new(ReferenceEqualityComparer.Instance)).Add(population);

        public void ReleasePopulationStructureCapture(object population) =>
            CapturedPopulationStructures?.Remove(population);

        public bool CapturePopulationRole(PopulationColumnBase column) =>
            (CapturedPopulationRoles ??= new(ReferenceEqualityComparer.Instance)).Add(column);

        public void ReleasePopulationRoleCapture(PopulationColumnBase column) => CapturedPopulationRoles?.Remove(column);

        public void InheritPopulationCaptures(ScopeState child)
        {
            if (child.CapturedPopulationPages is not null)
            {
                foreach (var page in child.CapturedPopulationPages)
                {
                    (CapturedPopulationPages ??= []).Add(page);
                }
            }

            if (child.CapturedPopulationBuffers is not null)
            {
                foreach (var buffer in child.CapturedPopulationBuffers)
                {
                    (CapturedPopulationBuffers ??= []).Add(buffer);
                }
            }

            if (child.CapturedPopulationStructures is not null)
            {
                foreach (var population in child.CapturedPopulationStructures)
                {
                    (CapturedPopulationStructures ??= new(ReferenceEqualityComparer.Instance)).Add(population);
                }
            }

            if (child.CapturedPopulationRoles is not null)
            {
                foreach (var column in child.CapturedPopulationRoles)
                {
                    (CapturedPopulationRoles ??= new(ReferenceEqualityComparer.Instance)).Add(column);
                }
            }
        }
    }

    internal readonly record struct PopulationPageKey(PopulationColumnBase Column, int BufferIndex, int PageIndex);
    internal readonly record struct PopulationBufferKey(PopulationColumnBase Column, int BufferIndex);

    private enum UndoKind : byte
    {
        EntitySnapshot,
        Property,
        DataProperty,
        Spatial,
        ColumnUndo,
        EditMarker,
        Added,
        Removed,
        Modifier,
        Grants,
        Role,
        Required,
        PopulationDefinition,
    }

    private readonly struct UndoRecord
    {
        public UndoKind Kind { get; private init; }
        public Entity? Entity { get; private init; }
        public Entity? Snapshot { get; private init; }
        public EntityPropertyUndo PropertyValue { get; private init; }
        public IColumnUndoBatch? ColumnUndoBatch { get; private init; }
        public int EntityId { get; private init; }
        public int Index { get; private init; }
        public string? Property { get; private init; }
        public PropertyModifier[]? Modifiers { get; private init; }
        public PropertyGrant[]? Grants { get; private init; }
        public Bounds2 SpatialBounds { get; private init; }
        public bool HadSpatialBounds { get; private init; }
        public double NumericValue { get; private init; }
        public PopulationRegistration? Population { get; private init; }

        public static UndoRecord ForEntitySnapshot(Entity entity, Entity snapshot) => new()
        {
            Kind = UndoKind.EntitySnapshot,
            Entity = entity,
            Snapshot = snapshot,
        };

        public static UndoRecord ForProperty(
            Entity entity,
            string property,
            EntityPropertyUndo previousValue) => new()
            {
                Kind = UndoKind.Property,
                Entity = entity,
                Property = property,
                PropertyValue = previousValue,
            };

        public static UndoRecord ForDataProperty(DataEntity entity, string property, double previousValue) => new()
        {
            Kind = UndoKind.DataProperty,
            Entity = entity,
            Property = property,
            NumericValue = previousValue,
        };

        public static UndoRecord ForColumnUndo(IColumnUndoBatch batch) => new()
        {
            Kind = UndoKind.ColumnUndo,
            ColumnUndoBatch = batch,
        };

        public static UndoRecord ForEditMarker() => new()
        {
            Kind = UndoKind.EditMarker,
        };

        public static UndoRecord ForAdded(Entity entity, int previousId) => new()
        {
            Kind = UndoKind.Added,
            Entity = entity,
            Index = previousId,
        };

        public static UndoRecord ForSpatial(int entityId, bool hadBounds, Bounds2 bounds) => new()
        {
            Kind = UndoKind.Spatial,
            EntityId = entityId,
            HadSpatialBounds = hadBounds,
            SpatialBounds = bounds,
        };

        public static UndoRecord ForRemoved(int index, Entity entity, bool hadSpatialBounds, Bounds2 spatialBounds) => new()
        {
            Kind = UndoKind.Removed,
            Entity = entity,
            Index = index,
            HadSpatialBounds = hadSpatialBounds,
            SpatialBounds = spatialBounds,
        };

        public static UndoRecord ForModifier(
            (int EntityId, string Property) key,
            PropertyModifier[]? items) => new()
            {
                Kind = UndoKind.Modifier,
                EntityId = key.EntityId,
                Property = key.Property,
                Modifiers = items,
            };

        public static UndoRecord ForGrants(int entityId, PropertyGrant[]? grants) => new()
        {
            Kind = UndoKind.Grants,
            EntityId = entityId,
            Grants = grants,
        };

        public static UndoRecord ForRole(int role, int entityId) => new()
        {
            Kind = UndoKind.Role,
            Index = role,
            EntityId = entityId,
        };

        public static UndoRecord ForRequired(int entityId) => new()
        {
            Kind = UndoKind.Required,
            EntityId = entityId,
        };

        public static UndoRecord ForPopulationDefinition(PopulationRegistration population) => new()
        {
            Kind = UndoKind.PopulationDefinition,
            Population = population,
        };

        public readonly void Undo(World world)
        {
            switch (Kind)
            {
                case UndoKind.EntitySnapshot:
                    Entity!.RestoreFrom(Snapshot!);
                    break;
                case UndoKind.Property:
                    Entity!.RestoreJournaledProperty(Property!, PropertyValue);
                    break;
                case UndoKind.DataProperty:
                    ((DataEntity)Entity!).RestoreDataProperty(Property!, NumericValue);
                    break;
                case UndoKind.Spatial:
                    world.RestoreSpatialBounds(EntityId, HadSpatialBounds, SpatialBounds);
                    break;
                case UndoKind.ColumnUndo:
                    ColumnUndoBatch!.Undo();
                    break;
                case UndoKind.EditMarker:
                    break;
                case UndoKind.Added:
                    var added = Entity!;
                    world.Detach(added);
                    added.Id = Index;
                    break;
                case UndoKind.Removed:
                    var removed = Entity!;
                    world._entities.Insert(Index, removed);
                    world._byId[removed.Id] = removed;
                    world.AttachStructuralIndexes(removed, Index);
                    removed.OwnerWorld = world;
                    if (removed is IWorldStateAttachment attachment)
                    {
                        attachment.AttachToWorld(world);
                    }

                    if (HadSpatialBounds)
                    {
                        world.RestoreSpatialBounds(removed.Id, true, SpatialBounds);
                    }

                    world.MarkPopulationDirty();
                    world.UpdateEntityIndex(removed, present: true, includeGrants: true);
                    break;
                case UndoKind.Modifier:
                    world.RestoreModifierKey((EntityId, Property!), Modifiers);
                    break;
                case UndoKind.Grants:
                    world.RestoreGrants(EntityId, Grants);
                    break;
                case UndoKind.Role:
                    world._roles[Index] = EntityId;
                    break;
                case UndoKind.Required:
                    world._required.Remove(EntityId);
                    break;
                case UndoKind.PopulationDefinition:
                    world._populationsByKind.Remove(Population!.Kind);
                    world._populationsById.Remove(Population.Id);
                    Population.DetachWorld();
                    break;
                default:
                    throw new InvalidOperationException($"Unknown undo kind: {Kind}.");
            }
        }

        public readonly void Release() => ColumnUndoBatch?.Release();
    }

    private readonly record struct CachedModifierApplication(
        double BaseValue,
        long Version,
        double Effective);

    private void Detach(Entity entity)
    {
        UpdateEntityIndex(entity, present: false, includeGrants: true);
        if (entity is IWorldStateAttachment attachment)
        {
            attachment.DetachFromWorld(this);
        }

        RemoveEntityAt(_entityPositions[entity.Id]);
        _byId.Remove(entity.Id);
        for (var role = 1; role < _roles.Length; role++)
        {
            if (_roles[role] == entity.Id)
            {
                _roles[role] = 0;
            }
        }

        _required.Remove(entity.Id);
        entity.OwnerWorld = null;
        MarkPopulationDirty();
    }
}

internal readonly struct WorldScope(World world, World.ScopeState scope, ulong generation) : IDisposable
{
    public bool HasChanges => world.HasChanges(scope, generation);

    public void OnRollback(Action action) => world.RegisterRollbackAction(scope, generation, action);

    public void OnCommit(Action action) => world.RegisterCommitAction(scope, generation, action);

    public void Commit() => world.EndScope(scope, generation, rollback: false);

    public void Rollback() => world.EndScope(scope, generation, rollback: true);

    // WHY: an unwound scope must never publish partial work; every success path commits explicitly.
    public void Dispose() => Rollback();
}

public readonly struct WorldTransaction : IDisposable
{
    private readonly WorldScope _scope;
    private readonly bool _owned;

    internal WorldTransaction(WorldScope scope) : this(scope, owned: true)
    {
    }

    private WorldTransaction(WorldScope scope, bool owned)
    {
        _scope = scope;
        _owned = owned;
    }

    public bool HasChanges => _scope.HasChanges;

    /// <summary>
    /// Shares this scope with a batch that owns commit and rollback, leaving registered rollback and
    /// commit callbacks intact.
    /// </summary>
    internal WorldTransaction AsBatchMember() => new(_scope, owned: false);

    /// <summary>Registers cleanup for external state when this scope or an enclosing scope rolls back.</summary>
    public void OnRollback(Action action) => _scope.OnRollback(action);

    /// <summary>Registers external publication to run only after this scope and all enclosing scopes commit.</summary>
    public void OnCommit(Action action) => _scope.OnCommit(action);

    public void Commit()
    {
        if (_owned) _scope.Commit();
    }

    public void Rollback()
    {
        if (_owned) _scope.Rollback();
    }

    public void Dispose()
    {
        if (_owned) _scope.Dispose();
    }
}
