using System.Collections.Immutable;
using Systemic.Kernel;

namespace Systemic.Tests.Kernel;

public sealed class PopulationScopedAccessTests
{
    private static readonly PopulationColumnKey<int> s_valueKey =
        new("scoped-value", 1, PopulationCodecs.Int32);

    private static readonly PopulationColumnKey<int> s_otherValueKey =
        new("scoped-other-value", 1, PopulationCodecs.Int32);

    [Fact]
    public void SerialAccessExposesTheWholeReadAndOnlyTheAssignedWriteRange()
    {
        var population = new World(seed: 801, valueScale: 1).DefinePopulation<ScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(5);

        population.WithAccess(new PopulationRange(1, 2), values, static (PopulationColumn<int> column, scoped ref PopulationAccessScope<ScopedKind> access) =>
        {
            access.EditRange(column).Fill(7);
        });

        population.WithAccess(new PopulationRange(0, 5), values, static (PopulationColumn<int> column, scoped ref PopulationAccessScope<ScopedKind> access) =>
        {
            Assert.Equal([0, 7, 7, 0, 0], access.ReadAll(column).ToArray());
        });
    }

    [Fact]
    public void ALeaseCannotBeDisposedWhileAScopedCallbackIsRunning()
    {
        var population = new World(seed: 802, valueScale: 1).DefinePopulation<ScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(1);
        using var lease = population.Borrow();

        lease.WithAccess(
            new PopulationRange(0, 1), (lease, values),
            static ((PopulationLease<ScopedKind> lease, PopulationColumn<int> values) target, scoped ref PopulationAccessScope<ScopedKind> access) =>
            {
                Assert.Throws<InvalidOperationException>(target.lease.Dispose);
                access.EditRange(target.values).Fill(2);
            });
    }

    [Fact]
    public void ScheduledParallelAccessRequiresDeclaredColumnWriteAccess()
    {
        var world = new World(seed: 803, valueScale: 1);
        var population = world.DefinePopulation<ScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(100);
        var system = new ScopedSystem(population, values, [SystemAccess.WriteColumn<ScopedKind, int>(s_valueKey)]);
        var schedule = new DeterministicSystemScheduleBuilder(workerCount: 4).Add(system).Build();

        schedule.Execute(world, tick: 1);

        using var lease = population.Borrow();
        lease.WithAccess(new PopulationRange(0, 100), values, static (PopulationColumn<int> column, scoped ref PopulationAccessScope<ScopedKind> access) =>
            Assert.All(access.ReadAll(column).ToArray(), value => Assert.Equal(9, value)));
    }

    [Fact]
    public void ScheduledWriteWithoutAColumnDeclarationIsRejected()
    {
        var world = new World(seed: 804, valueScale: 1);
        var population = world.DefinePopulation<ScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(1);
        var system = new ScopedSystem(population, values, [SystemAccess.ReadColumn<ScopedKind, int>(s_valueKey)]);
        var schedule = new DeterministicSystemScheduleBuilder(workerCount: 1).Add(system).Build();

        Assert.Throws<InvalidOperationException>(() => schedule.Execute(world, tick: 1));
    }

    [Fact]
    public void AScheduledLeaseCannotBypassColumnDeclarationsThroughSerialAccess()
    {
        var world = new World(seed: 805, valueScale: 1);
        var population = world.DefinePopulation<ScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(1);
        var system = new SerialWithAccessSystem(population, values);
        var schedule = new DeterministicSystemScheduleBuilder(workerCount: 1).Add(system).Build();

        Assert.Throws<InvalidOperationException>(() => schedule.Execute(world, tick: 1));
    }

    [Fact]
    public void ParallelReadWriteAccessCannotReadTheFullSingleBufferedColumn()
    {
        var world = new World(seed: 807, valueScale: 1);
        var population = world.DefinePopulation<ScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(8);
        var system = new ReadAllSystem(population, values,
        [
            SystemAccess.ReadColumn<ScopedKind, int>(s_valueKey),
            SystemAccess.WriteColumn<ScopedKind, int>(s_valueKey)
        ]);
        using var schedule = new DeterministicSystemScheduleBuilder(workerCount: 2).Add(system).Build();

        Assert.Throws<InvalidOperationException>(() => schedule.Execute(world, tick: 1));
    }

    [Fact]
    public void ASecondLeaseCannotBypassParallelSingleBufferReadRestrictions()
    {
        var world = new World(seed: 811, valueScale: 1);
        var population = world.DefinePopulation<ScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(8);
        var access = new[]
        {
            SystemAccess.ReadColumn<ScopedKind, int>(s_valueKey),
            SystemAccess.WriteColumn<ScopedKind, int>(s_valueKey)
        };
        var system = new AliasedReadSystem(population, values, access);
        using var schedule = new DeterministicSystemScheduleBuilder(workerCount: 2).Add(system).Build();

        Assert.Throws<InvalidOperationException>(() => schedule.Execute(world, tick: 1));
    }

    [Fact]
    public void WriteWorldDoesNotGrantTypedPopulationColumnAccess()
    {
        var world = new World(seed: 808, valueScale: 1);
        var population = world.DefinePopulation<ScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(1);
        var system = new ScopedSystem(population, values, [SystemAccess.WriteWorld]);
        using var schedule = new DeterministicSystemScheduleBuilder(workerCount: 1).Add(system).Build();

        Assert.Throws<InvalidOperationException>(() => schedule.Execute(world, tick: 1));
    }

    [Fact]
    public void ScopeGuardExpiresAfterTheCallbackReturns()
    {
        var population = new World(seed: 809, valueScale: 1).DefinePopulation<ScopedKind>();
        population.AddRange(1);
        var holder = new GuardHolder();
        population.WithAccess(new PopulationRange(0, 1), holder,
            static (GuardHolder state, scoped ref PopulationAccessScope<ScopedKind> scope) => state.Guard = scope.Guard);

        Assert.Throws<InvalidOperationException>(holder.Guard.Validate);
    }

    [Fact]
    public void AScopeRejectsAColumnFromAnotherPopulation()
    {
        var world = new World(seed: 810, valueScale: 1);
        var first = world.DefinePopulation<ScopedKind>();
        var second = world.DefinePopulation<OtherScopedKind>();
        var secondColumn = second.Column(s_valueKey);
        first.AddRange(1);
        second.AddRange(1);
        using var secondLease = second.Borrow();

        first.WithAccess(new PopulationRange(0, 1), (secondLease, secondColumn),
            static ((PopulationLease<OtherScopedKind> Lease, PopulationColumn<int> Column) state,
                scoped ref PopulationAccessScope<ScopedKind> scope) =>
                Assert.Equal([0], scope.ReadAll(state.Lease, state.Column).ToArray()));
    }

    [Fact]
    public void DeclaredAccessMatchesSchemaIdsThatContainTheColumnSeparator()
    {
        var population = new World(seed: 813, valueScale: 1).DefinePopulation<DelimiterScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(1);
        ImmutableArray<SystemAccess> access = [SystemAccess.WriteColumn<DelimiterScopedKind, int>(s_valueKey)];
        var lifetime = population.StorageIdentity.SerialScopeLifetime;
        var group = new object();
        var generation = population.BeginScheduledRangeScope(new PopulationRange(0, 1), lifetime, access, group);
        var scope = new PopulationAccessScope<DelimiterScopedKind>(population, lifetime, generation);

        try
        {
            scope.EditRange(values)[0] = 42;
        }
        finally
        {
            population.EndScheduledRangeScope(lifetime, generation, group);
        }

        population.WithAccess(new PopulationRange(0, 1), values,
            static (PopulationColumn<int> column, scoped ref PopulationAccessScope<DelimiterScopedKind> accessScope) =>
                Assert.Equal(42, accessScope.ReadAll(column)[0]));
    }

    [Fact]
    public void PopulationAccessScopesExpireAndRejectTheWrongThread()
    {
        var population = new World(seed: 806, valueScale: 1).DefinePopulation<ScopedKind>();
        var values = population.Column(s_valueKey);
        population.AddRange(1);
        using var lease = population.Borrow();
        var lifetime = population.StorageIdentity.SerialScopeLifetime;
        var generation = lease.BeginScopedAccess(new PopulationRange(0, 1), lifetime, null, parallel: false, group: null);
        var stale = new PopulationAccessScope<ScopedKind>(lease, lifetime, generation);

        try
        {
            Exception? wrongThread = null;
            var worker = new Thread(() =>
            {
                try { lifetime.Validate(generation); }
                catch (Exception exception) { wrongThread = exception; }
            });
            worker.Start();
            worker.Join();
            Assert.IsType<InvalidOperationException>(wrongThread);
        }
        finally
        {
            lease.EndScopedAccess(lifetime, generation, parallel: false, group: null);
        }

        lease.WithAccess(new PopulationRange(0, 1), values,
            static (PopulationColumn<int> column, scoped ref PopulationAccessScope<ScopedKind> scope) =>
                scope.EditRange(column)[0] = 3);
        Exception? staleUse = null;
        try { _ = stale.ReadAll(values); }
        catch (Exception exception) { staleUse = exception; }
        Assert.IsType<InvalidOperationException>(staleUse);
    }

    [Fact]
    public void ScheduledPopulationLeaseCapturesTheRowCountItProtects()
    {
        var population = new World(seed: 812, valueScale: 1).DefinePopulation<ScopedKind>();
        population.AddRange(2);

        var rowCount = population.BeginScheduledRangeAccess();
        try
        {
            Assert.Equal(2, rowCount);
            Assert.Throws<InvalidOperationException>(() => population.AddRange(1));
        }
        finally
        {
            population.EndScheduledRangeAccess();
        }
    }

    [Fact]
    public void PopulationColumnTypesExposeNoRawBorrowedViews()
    {
        Assert.Null(typeof(PopulationColumn<int>).GetProperty("ReadOnlySpan"));
        Assert.Null(typeof(PopulationColumn<int>).GetProperty("ReadOnlyMemory"));
        Assert.Null(typeof(PopulationColumn<int>).GetMethod("EditRange"));
        Assert.Null(typeof(PopulationColumn<int>).GetMethod("EditMemory"));
        Assert.Null(typeof(DoubleBufferedPopulationColumn<int>).GetProperty("Current"));
        Assert.Null(typeof(DoubleBufferedPopulationColumn<int>).GetProperty("CurrentMemory"));
        Assert.Null(typeof(DoubleBufferedPopulationColumn<int>).GetMethod("EditNext"));
        Assert.Null(typeof(DoubleBufferedPopulationColumn<int>).GetMethod("EditNextMemory"));
        Assert.Null(typeof(PopulationLease<ScopedKind>).GetMethod("Read"));
        Assert.Null(typeof(PopulationLease<ScopedKind>).GetMethod("Edit"));
    }

    [Fact]
    public void ParallelScopesAdmitDisjointDeclaredColumnsOfOnePopulation()
    {
        var world = new World(seed: 814, valueScale: 1);
        var population = world.DefinePopulation<ScopedKind>();
        population.AddRange(4);
        var storage = population.StorageIdentity;
        var firstGroup = new object();
        var secondGroup = new object();
        ImmutableArray<SystemAccess> first = [SystemAccess.WriteColumn<ScopedKind, int>(s_valueKey)];
        ImmutableArray<SystemAccess> second = [SystemAccess.WriteColumn<ScopedKind, int>(s_otherValueKey)];

        storage.EnterScope(parallel: true, firstGroup, first);
        try
        {
            storage.EnterScope(parallel: true, secondGroup, second);
            try
            {
                Assert.Equal(4, storage.Count);
            }
            finally
            {
                storage.ExitScope(parallel: true, secondGroup);
            }
        }
        finally
        {
            storage.ExitScope(parallel: true, firstGroup);
        }

        Assert.False(storage.HasActiveScopes);
    }

    [Fact]
    public void ParallelScopesStillRejectTwoWritersOfOneColumn()
    {
        var world = new World(seed: 815, valueScale: 1);
        var population = world.DefinePopulation<ScopedKind>();
        population.AddRange(2);
        var storage = population.StorageIdentity;
        ImmutableArray<SystemAccess> write = [SystemAccess.WriteColumn<ScopedKind, int>(s_valueKey)];

        storage.EnterScope(parallel: true, new object(), write);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                storage.EnterScope(parallel: true, new object(), write));
            Assert.Throws<InvalidOperationException>(() =>
                storage.EnterScope(parallel: true, new object(), access: null));
            Assert.Throws<InvalidOperationException>(() =>
                storage.EnterScope(parallel: false, group: null));
        }
        finally
        {
            storage.ExitScope(parallel: true);
        }
    }

    [Fact]
    public void ParallelScopesOfOnePartitionGroupMayStillOverlap()
    {
        var world = new World(seed: 816, valueScale: 1);
        var population = world.DefinePopulation<ScopedKind>();
        population.AddRange(2);
        var storage = population.StorageIdentity;
        var group = new object();
        ImmutableArray<SystemAccess> write = [SystemAccess.WriteColumn<ScopedKind, int>(s_valueKey)];

        storage.EnterScope(parallel: true, group, write);
        try
        {
            storage.EnterScope(parallel: true, group, write);
            storage.ExitScope(parallel: true, group);
        }
        finally
        {
            storage.ExitScope(parallel: true, group);
        }

        Assert.False(storage.HasActiveScopes);
    }

    [Fact]
    public void TheScheduleAndPopulationScopesShareOneAccessConflictRule()
    {
        ImmutableArray<SystemAccess> read = [SystemAccess.ReadColumn<ScopedKind, int>(s_valueKey)];
        ImmutableArray<SystemAccess> write = [SystemAccess.WriteColumn<ScopedKind, int>(s_valueKey)];
        ImmutableArray<SystemAccess> otherWrite = [SystemAccess.WriteColumn<ScopedKind, int>(s_otherValueKey)];

        Assert.False(SystemAccess.Conflicts(read, read));
        Assert.True(SystemAccess.Conflicts(read, write));
        Assert.True(SystemAccess.Conflicts(write, write));
        Assert.False(SystemAccess.Conflicts(write, otherWrite));
        Assert.False(SystemAccess.Conflicts([SystemAccess.DeferWorldWrites], [SystemAccess.DeferWorldWrites]));
        Assert.False(SystemAccess.Conflicts([SystemAccess.ReadWorld], [SystemAccess.DeferWorldWrites]));
        Assert.True(SystemAccess.Conflicts([SystemAccess.WriteWorld], read));
    }

    private readonly struct ScopedKind : IPopulationKind
    {
        public static string Id => "test.scoped";
        public static int Version => 1;
    }

    private readonly struct OtherScopedKind : IPopulationKind
    {
        public static string Id => "test.other-scoped";
        public static int Version => 1;
    }

    private readonly struct DelimiterScopedKind : IPopulationKind
    {
        public static string Id => "test/scoped/column:kind";
        public static int Version => 1;
    }

    private sealed class GuardHolder
    {
        public PopulationAccessGuard Guard;
    }

    private sealed class ReadAllSystem(
        Population<ScopedKind> population,
        PopulationColumn<int> values,
        IReadOnlyList<SystemAccess> access) : IScheduledKernelSystem
    {
        public string Id => "parallel-full-read";
        public IReadOnlyList<SystemAccess> Access => access;

        public void Execute(ScheduledSystemContext context) =>
            context.ForEachPopulationRange(context.BorrowPopulation(population), values,
                static (PopulationColumn<int> column, int _, scoped ref PopulationAccessScope<ScopedKind> scope) =>
                    scope.ReadAll(column));
    }

    private sealed class AliasedReadSystem(
        Population<ScopedKind> population,
        PopulationColumn<int> values,
        IReadOnlyList<SystemAccess> access) : IScheduledKernelSystem
    {
        public string Id => "parallel-aliased-full-read";
        public IReadOnlyList<SystemAccess> Access => access;

        public void Execute(ScheduledSystemContext context)
        {
            var lease = context.BorrowPopulation(population);
            var alias = context.BorrowPopulation(population);
            context.ForEachPopulationRange(lease, (alias, values),
                static ((PopulationLease<ScopedKind> Lease, PopulationColumn<int> Values) state,
                    int partition, scoped ref PopulationAccessScope<ScopedKind> scope) =>
                    scope.ReadAll(state.Lease, state.Values));
        }
    }

    private sealed class ScopedSystem(
        Population<ScopedKind> population,
        PopulationColumn<int> values,
        IReadOnlyList<SystemAccess> access) : IScheduledKernelSystem
    {
        public string Id => "scoped-access";
        public IReadOnlyList<SystemAccess> Access => access;

        public void Execute(ScheduledSystemContext context) =>
            context.ForEachPopulationRange(GetPopulationLease(context), values,
                static (PopulationColumn<int> column, int _, scoped ref PopulationAccessScope<ScopedKind> scope) => scope.EditRange(column).Fill(9));

        private PopulationLease<ScopedKind> GetPopulationLease(ScheduledSystemContext context) =>
            context.BorrowPopulation(population);
    }

    private sealed class SerialWithAccessSystem(
        Population<ScopedKind> population,
        PopulationColumn<int> values) : IScheduledKernelSystem
    {
        public string Id => "serial-scoped-access";
        public IReadOnlyList<SystemAccess> Access => [SystemAccess.ReadColumn<ScopedKind, int>(s_valueKey)];

        public void Execute(ScheduledSystemContext context)
        {
            population.WithAccess(new PopulationRange(0, population.Count), values,
                static (PopulationColumn<int> column, scoped ref PopulationAccessScope<ScopedKind> scope) =>
                    scope.EditRange(column).Fill(1));
        }
    }
}
