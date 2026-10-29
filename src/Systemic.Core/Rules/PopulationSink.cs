using Systemic.Kernel;

namespace Systemic.Rules;

/// <summary>
/// Turns "N rows crossed a threshold" into one fact, so a crowd rule's fact volume does not scale
/// with the number of rows it is about.
/// </summary>
/// <remarks>
/// Per-row evaluation is affordable at a million rows; emitting a fact per row is not. A sink owns
/// one column, one threshold and optionally one schema tag, counts the matching rows in a single
/// pass when it is collected, and appends at most one row-addressed fact carrying that count as its
/// amount. Nothing is emitted while the count is unchanged, so a steady crowd costs no facts at all.
/// <para>
/// The fact is addressed to a caller-chosen anchor row of the same population, which is what makes
/// it dispatchable through the existing rule path: rules react to it with
/// <see cref="RuleSubject.FactRow"/>. If the anchor despawns, the sink stops emitting rather than
/// reporting against a replaced row.
/// </para>
/// </remarks>
public sealed class PopulationSink
{
    private readonly World _world;
    private readonly string _eventType;
    private readonly PopulationColumnBase _column;
    private readonly PopulationTagAccess _tags;
    private readonly int _tagBit;
    private readonly double _threshold;
    private readonly PopulationRowReference _anchor;
    private int _lastCount = -1;
    private bool _sampled;
    private int _rollbackCount;
    private bool _rollbackSampled;
    private readonly Action _restoreBaseline;

    /// <summary>Creates a sink over one numeric column of one population.</summary>
    /// <param name="world">The world whose population the sink reads.</param>
    /// <param name="populationId">The population schema ID to aggregate.</param>
    /// <param name="columnId">A numeric column of that population.</param>
    /// <param name="threshold">Rows with a value at least this large are counted.</param>
    /// <param name="eventType">The fact type to raise, carrying the count as its amount.</param>
    /// <param name="anchor">A live row of the same population that receives the fact.</param>
    /// <param name="tag">An optional schema tag that a row must also carry to be counted.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The population, column, tag or anchor cannot be
    /// resolved, the column is not numeric, or the anchor belongs to another population.</exception>
    public PopulationSink(
        World world,
        string populationId,
        string columnId,
        double threshold,
        string eventType,
        PopulationRowReference anchor,
        string? tag = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentException.ThrowIfNullOrWhiteSpace(populationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(columnId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (!double.IsFinite(threshold))
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold,
                "A sink threshold must be finite.");
        }

        if (!string.Equals(anchor.PopulationId, populationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Population sink for '{populationId}' was given an anchor row of '{anchor.PopulationId}'; "
                + "the anchor must be a row of the population the sink aggregates.");
        }

        if (!world.TryGetPopulationColumn(populationId, columnId, out var column))
        {
            throw new InvalidOperationException(
                $"Population sink references column '{columnId}' of population '{populationId}', "
                + "which this world does not define.");
        }

        if (!PopulationNumeric.IsNumeric(column.ValueType))
        {
            throw new InvalidOperationException(
                $"Population sink references column '{columnId}' of population '{populationId}', "
                + $"which holds '{column.ValueType.Name}'; a sink needs a numeric column.");
        }

        var tags = default(PopulationTagAccess);
        var tagBit = -1;
        if (tag is not null)
        {
            if (!world.TryGetPopulationTags(populationId, out tags))
            {
                throw new InvalidOperationException(
                    $"Population sink references tag '{tag}' of population '{populationId}', "
                    + "which this world does not define.");
            }

            if (!tags.TryGetBit(tag, out tagBit))
            {
                throw new InvalidOperationException(
                    $"Population sink references tag '{tag}' of population '{populationId}', "
                    + $"which does not declare it. Declared tags: '{string.Join("', '", tags.Names.Span)}'.");
            }
        }

        if (!TryResolveAnchor(world, anchor))
        {
            throw new InvalidOperationException(
                $"Population sink anchor {anchor} is not a live row of population '{populationId}'.");
        }

        _world = world;
        _eventType = eventType;
        _column = column;
        _tags = tags;
        _tagBit = tagBit;
        _threshold = threshold;
        _anchor = anchor;
        _restoreBaseline = RestoreBaseline;
    }

    /// <summary>Gets the population schema this sink aggregates.</summary>
    public string PopulationId => _anchor.PopulationId;

    /// <summary>Gets the fact type this sink raises.</summary>
    public string EventType => _eventType;

    /// <summary>Gets the row this sink addresses its facts to.</summary>
    public PopulationRowReference Anchor => _anchor;

    /// <summary>Gets the row count reported by the last sample, or -1 before the first one.</summary>
    public int Count => _lastCount;

    /// <summary>Counts the matching rows now, without raising a fact, and makes that the baseline.</summary>
    public int Sample()
    {
        RestoreBaselineOnRollback();
        _lastCount = CountMatches();
        _sampled = true;
        return _lastCount;
    }

    /// <summary>Counts the matching rows and appends at most one fact when the count changed.</summary>
    /// <param name="facts">Destination list for the raised fact.</param>
    /// <returns>The number of facts appended: zero or one.</returns>
    /// <remarks>
    /// The bound is what makes the sink a sink: a million crossings and one crossing both cost one
    /// fact, and a count that has not moved costs none. A despawned anchor appends nothing.
    /// </remarks>
    public int Collect(List<SimulationFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (!TryResolveAnchor(_world, _anchor))
        {
            return 0;
        }

        var count = CountMatches();
        if (_sampled && count == _lastCount)
        {
            return 0;
        }

        RestoreBaselineOnRollback();
        _sampled = true;
        _lastCount = count;
        facts.Add(SimulationFact.ForRow(_eventType, _anchor, amount: count));
        return 1;
    }

    // WHY: the baseline lives outside the world's row undo, so a rolled-back tick would otherwise
    // keep the count it reported and the retry of the same change would raise no fact.
    private void RestoreBaselineOnRollback()
    {
        if (!_world.HasActiveScope)
        {
            return;
        }

        _rollbackCount = _lastCount;
        _rollbackSampled = _sampled;
        _world.TryRegisterRollbackAction(_restoreBaseline);
    }

    private void RestoreBaseline()
    {
        _lastCount = _rollbackCount;
        _sampled = _rollbackSampled;
    }

    private int CountMatches()
    {
        if (_tagBit < 0)
        {
            return _column.CountRowsAtLeast(_threshold);
        }

        var rows = _column.RowCount;
        var count = 0;
        for (var row = 0; row < rows; row++)
        {
            if (_tags.TryHasTagBit(row, _tagBit)
                && _column.TryGetRowValue(row, out var value)
                && value >= _threshold)
            {
                count++;
            }
        }

        return count;
    }

    private static bool TryResolveAnchor(World world, PopulationRowReference anchor) =>
        world.TryResolvePopulationRow(anchor.PopulationId, anchor.Slot, anchor.Generation, out _);
}
