namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

public sealed class ContainerOperationCustodyContractTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(20);
    private const string FinallyJoinPhase = "regression-finally-join";

    [Fact]
    public async Task PostStartDatabaseFailure_DisposesOwnerAndPreservesDatabaseError()
    {
        var database = new IOException("Controlled database initialization failure.");
        var owner = new ProbeOwner(() => Task.CompletedTask);
        Assert.Same(database, await Assert.ThrowsAsync<IOException>(() =>
            ContainerOperationCustody.InitializeAsync(owner, _ => Task.FromException(database), CancellationToken.None)));
        Assert.Equal(1, owner.Disposals);
    }

    [Fact]
    public async Task DatabaseAndCleanupFailures_AreBothPreserved()
    {
        var database = new IOException("Controlled database failure.");
        var cleanup = new InvalidOperationException("Controlled cleanup failure.");
        var owner = new ProbeOwner(() => Task.FromException(cleanup));
        Assert.Same(database, await Assert.ThrowsAsync<IOException>(() =>
            ContainerOperationCustody.InitializeAsync(owner, _ => Task.FromException(database), CancellationToken.None)));
        Assert.Contains(cleanup, Assert.IsType<Exception[]>(database.Data["maliev.cleanup.failures"]));
    }

    [Fact]
    public async Task InitializationIgnoresCancellation_RetainsOwnerUntilActualTaskFaultsThenCleans()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new IOException("Controlled late database fault.");
        var owner = new ProbeOwner(() => Task.CompletedTask);
        using var canceled = new CancellationTokenSource();
        var initialize = ContainerOperationCustody.InitializeAsync(owner, _ => actual.Task, canceled.Token, Budget);
        Exception? assertionFailure = null;
        try
        {
            await canceled.CancelAsync();
            var primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
            var custody = Assert.IsType<ContainerOperationCustody.RetainedOperation>(primary.Data["maliev.cleanup.custody"]);
            Assert.Contains(owner, custody.Owners);
            Assert.False(custody.Operation.IsCompleted);
            Assert.Equal(0, owner.Disposals);
            actual.SetException(late);
            await custody.Observation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, owner.Disposals);
            Assert.Contains(late, Assert.IsType<Exception[]>(primary.Data["maliev.initialization.eventual-failures"]));
            Assert.DoesNotContain(ContainerOperationCustody.Pending, item => item.Id == custody.Id);
        }
        catch (Exception failure)
        {
            assertionFailure = failure;
            throw;
        }
        finally
        {
            actual.TrySetResult();
            await JoinAfterReleasePreservingAssertionAsync(initialize, actual.Task, assertionFailure);
        }
    }

    [Fact]
    public async Task ActualDisposalTimeout_RetainsClientUntilActualTaskFaultIsObserved()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new object();
        var released = 0;
        var primary = new IOException("Controlled primary failure.");
        var cleanup = ContainerOperationCustody.CleanupPreservingAsync(primary, actual.Task, Budget, "disposal", [client]);
        Exception? assertionFailure = null;
        try
        {
            await cleanup;
            var custody = Assert.IsType<ContainerOperationCustody.RetainedOperation>(primary.Data["maliev.cleanup.custody"]);
            var release = ContainerOperationCustody.Retain(custody.Operation, "client-release", [client, custody], () => released++);
            Assert.Equal(0, released);
            Assert.Contains(client, release.Owners);
            Assert.False(actual.Task.IsCompleted);
            var late = new IOException("Controlled late disposal fault.");
            actual.SetException(late);
            await Task.WhenAll(custody.Observation, release.Observation).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, released);
            Assert.Contains(late, custody.EventualFailures);
            Assert.Contains(late, release.EventualFailures);
            Assert.Contains(ContainerOperationCustody.Pending, item => item.Id == custody.Id);
            Assert.Contains(ContainerOperationCustody.Pending, item => item.Id == release.Id);
        }
        catch (Exception failure)
        {
            assertionFailure = failure;
            throw;
        }
        finally
        {
            actual.TrySetResult();
            await JoinAfterReleasePreservingAssertionAsync(cleanup, actual.Task, assertionFailure, primary);
        }
    }

    [Fact]
    public async Task StartupIgnoresCancellation_DoesNotRemoveBeforeActualStartupSettles()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resource = new ProbeResource(_ => actual.Task, () => Task.CompletedTask);
        using var canceled = new CancellationTokenSource();
        var start = DisposableContainerStartup.StartAsync(() => resource, NoDelay, canceled.Token, Budget);
        Exception? assertionFailure = null;
        try
        {
            await canceled.CancelAsync();
            var primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            var custody = Assert.IsType<ContainerOperationCustody.RetainedOperation>(primary.Data["maliev.cleanup.custody"]);
            Assert.Equal(0, resource.Disposals);
            Assert.Contains(resource, custody.Owners);
            var late = new IOException("Controlled late startup fault.");
            actual.SetException(late);
            await custody.Observation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, resource.Disposals);
            Assert.Contains(late, Assert.IsType<Exception[]>(primary.Data["maliev.startup.eventual-failures"]));
        }
        catch (Exception failure)
        {
            assertionFailure = failure;
            throw;
        }
        finally
        {
            actual.TrySetResult();
            await JoinAfterReleasePreservingAssertionAsync(start, actual.Task, assertionFailure);
        }
    }

    [Fact]
    public async Task PairPrimaryAndBothCleanupFailures_PreservePrimaryAndAttemptBothOwners()
    {
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new IOException("Controlled first startup failure.");
        var firstCleanup = new InvalidOperationException("First cleanup failure.");
        var secondCleanup = new InvalidOperationException("Second cleanup failure.");
        var first = new ProbeResource(async token => { await secondEntered.Task.WaitAsync(token); throw primary; },
            () => Task.FromException(firstCleanup));
        var second = new ProbeResource(token => { secondEntered.SetResult(); return Task.Delay(Timeout.InfiniteTimeSpan, token); },
            () => Task.FromException(secondCleanup));
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() =>
            DisposableContainerStartup.StartPairAsync(() => first, () => second, NoDelay)));
        var failures = Assert.IsType<Exception[]>(primary.Data["maliev.cleanup.failures"]);
        Assert.Contains(firstCleanup, failures);
        Assert.Contains(secondCleanup, failures);
        Assert.True(first.Disposals > 0 && second.Disposals > 0);
    }

    [Fact]
    public async Task PairCanceledWithIgnoringSibling_ReturnsBoundedFailureAndRetainsActualCleanup()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new ProbeResource(_ => actual.Task, () => Task.CompletedTask);
        var second = new ProbeResource(_ => Task.CompletedTask, () => Task.CompletedTask);
        using var canceled = new CancellationTokenSource();
        var start = DisposableContainerStartup.StartPairAsync(() => first, () => second, NoDelay, canceled.Token, Budget);
        Exception? assertionFailure = null;
        try
        {
            await canceled.CancelAsync();
            var primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
            var custody = Assert.IsType<ContainerOperationCustody.RetainedOperation>(primary.Data["maliev.cleanup.custody"]);
            Assert.False(custody.Operation.IsCompleted);
            Assert.Equal(0, first.Disposals);
            var late = new IOException("Controlled late sibling startup fault.");
            actual.SetException(late);
            await custody.Observation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.True(first.Disposals > 0 && second.Disposals > 0);
            Assert.Contains(late, Assert.IsType<Exception[]>(primary.Data["maliev.startup.eventual-failures"]));
        }
        catch (Exception failure)
        {
            assertionFailure = failure;
            throw;
        }
        finally
        {
            actual.TrySetResult();
            await JoinAfterReleasePreservingAssertionAsync(start, actual.Task, assertionFailure);
        }
    }

    private static Task NoDelay(TimeSpan delay, CancellationToken token) => Task.CompletedTask;

    private static async Task JoinAfterReleasePreservingAssertionAsync(Task operation, Task actual,
        Exception? assertionFailure, Exception? primary = null, TimeSpan? waitBudget = null,
        Action<ContainerOperationCustody.RetainedOperation>? onJoining = null)
    {
        var join = JoinAfterReleaseAsync(operation, actual, primary, onJoining);
        if (assertionFailure is null) await join; // Cleanup failure must fail an otherwise successful test.
        else await ContainerOperationCustody.CleanupPreservingAsync(assertionFailure, join,
            waitBudget ?? TimeSpan.FromSeconds(2), FinallyJoinPhase, [operation, actual]);
    }

    private static async Task JoinAfterReleaseAsync(Task operation, Task actual, Exception? primary = null,
        Action<ContainerOperationCustody.RetainedOperation>? onJoining = null)
    {
        try { await actual.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception) when (actual.IsCompleted) { /* Observe the controlled late fault, if it was released. */ }
        try { await operation.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception failure) when (operation.IsCompleted) { primary ??= failure; }

        var owned = new HashSet<object> { operation, actual };
        if (primary is not null && ContainerOperationCustody.CustodyFor(primary) is { } custody
            && custody.Phase != FinallyJoinPhase)
        {
            owned.Add(custody);
            foreach (var owner in custody.Owners) owned.Add(owner);
            await custody.Observation.WaitAsync(TimeSpan.FromSeconds(2));
        }
        var joined = new HashSet<Guid>();
        // Follow only this test's exact owner graph, including client and linked-cancellation-source release.
        while (ContainerOperationCustody.Pending.FirstOrDefault(candidate =>
            candidate.Phase != FinallyJoinPhase && !joined.Contains(candidate.Id)
            && candidate.Owners.Any(owned.Contains)) is { } entry)
        {
            joined.Add(entry.Id);
            owned.Add(entry);
            foreach (var owner in entry.Owners) owned.Add(owner);
            onJoining?.Invoke(entry);
            await entry.Observation.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task FinallyJoinTimeout_ExcludesItsOwnWrapperButJoinsRealLateFaultAndPreservesAssertion()
    {
        var actual = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var realCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredRealJoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = new System.Collections.Concurrent.ConcurrentQueue<Guid>();
        var primary = new Xunit.Sdk.XunitException("Controlled primary assertion failure.");
        var real = ContainerOperationCustody.Retain(realCleanup.Task, "controlled-real-cleanup", [actual.Task]);
        var fail = FailAndJoinAsync();
        try
        {
            Assert.Same(primary, await Assert.ThrowsAsync<Xunit.Sdk.XunitException>(() => fail));
            var wrapper = Assert.IsType<ContainerOperationCustody.RetainedOperation>(ContainerOperationCustody.CustodyFor(primary));
            Assert.Equal(FinallyJoinPhase, wrapper.Phase);
            Assert.False(wrapper.Operation.IsCompleted);
            actual.SetResult(); // Resume traversal only after its own timed-out wrapper is registered.
            await enteredRealJoin.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(wrapper.Operation.IsCompleted);
            var late = new IOException("Controlled real cleanup late fault.");
            realCleanup.SetException(late);
            await wrapper.Observation.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Contains(real.Id, selected);
            Assert.DoesNotContain(wrapper.Id, selected);
            Assert.Contains(late, real.EventualFailures);
            Assert.Empty(wrapper.EventualFailures);
            Assert.True(wrapper.Operation.IsCompletedSuccessfully);
            Assert.Single(ContainerOperationCustody.FailuresFor(primary, "maliev.cleanup.failures"));
        }
        finally
        {
            actual.TrySetResult();
            realCleanup.TrySetResult();
            try { await fail.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Xunit.Sdk.XunitException failure) when (ReferenceEquals(failure, primary)) { }
            await real.Observation.WaitAsync(TimeSpan.FromSeconds(2));
            if (ContainerOperationCustody.CustodyFor(primary) is { } wrapper)
                await wrapper.Observation.WaitAsync(TimeSpan.FromSeconds(2));
        }

        async Task FailAndJoinAsync()
        {
            try { throw primary; }
            catch (Exception assertion)
            {
                await JoinAfterReleasePreservingAssertionAsync(Task.CompletedTask, actual.Task, assertion,
                    waitBudget: Budget, onJoining: entry =>
                    {
                        selected.Enqueue(entry.Id);
                        if (entry.Id == real.Id) enteredRealJoin.TrySetResult();
                    });
                throw;
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostileExceptionData_CannotReplaceStartupPrimaryOrPreventCustody(bool readOnly)
    {
        var primary = new HostileDataException(readOnly);
        var cleanup = new IOException("Controlled cleanup fault with hostile primary metadata.");
        var resource = new ProbeResource(_ => Task.FromException(primary), () => Task.FromException(cleanup));
        Assert.Same(primary, await Assert.ThrowsAsync<HostileDataException>(() =>
            DisposableContainerStartup.StartAsync(() => resource, NoDelay)));
        Assert.Equal(1, resource.Disposals);
        Assert.Contains(cleanup, ContainerOperationCustody.FailuresFor(primary, "maliev.cleanup.failures"));
        Assert.NotEmpty(ContainerOperationCustody.FailuresFor(primary, "maliev.evidence.metadata.failures"));
        var custody = Assert.IsType<ContainerOperationCustody.RetainedOperation>(ContainerOperationCustody.CustodyFor(primary));
        Assert.Contains(resource, custody.Owners);
        Assert.True(custody.Operation.IsFaulted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostileExceptionData_CannotSkipPostStartOwnerCleanup(bool readOnly)
    {
        var primary = new HostileDataException(readOnly);
        var owner = new ProbeOwner(() => Task.CompletedTask);
        Assert.Same(primary, await Assert.ThrowsAsync<HostileDataException>(() =>
            ContainerOperationCustody.InitializeAsync(owner, _ => Task.FromException(primary), CancellationToken.None)));
        Assert.Equal(1, owner.Disposals);
        Assert.Contains(primary, ContainerOperationCustody.FailuresFor(primary, "maliev.initialization.eventual-failures"));
        Assert.NotEmpty(ContainerOperationCustody.FailuresFor(primary, "maliev.evidence.metadata.failures"));
    }

    [Fact]
    public async Task CancellationCallbackFault_CannotMaskStartupPrimaryOrSkipEitherOwnerCleanup()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new IOException("Controlled startup failure before cancellation callback fault.");
        var callback = new InvalidOperationException("Controlled cancellation callback fault.");
        var first = new ProbeResource(async token => { await entered.Task.WaitAsync(token); throw primary; }, () => Task.CompletedTask);
        var second = new ProbeResource(async token =>
        {
            using var registration = token.Register(() => throw callback);
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, () => Task.CompletedTask);
        Assert.Same(primary, await Assert.ThrowsAsync<IOException>(() =>
            DisposableContainerStartup.StartPairAsync(() => first, () => second, NoDelay)));
        Assert.Equal(1, first.Disposals);
        Assert.Equal(1, second.Disposals);
        Assert.Contains(callback, ContainerOperationCustody.FailuresFor(primary, "maliev.cancellation.callback.failures"));
    }

    [Fact]
    public async Task ActualFixtureDisposeAfterTimedOutInitialization_WaitsForActualDatabaseOperation()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualDatabase = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new ProbeOwner(() => Task.CompletedTask);
        var fixture = new Controllers.QualificationBridgeConsumerFixture(
            _ => Task.FromResult<IAsyncDisposable>(owner),
            _ => { entered.SetResult(); return actualDatabase.Task; }, TimeSpan.FromMilliseconds(100), Budget);
        var initialize = fixture.InitializeAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var primary = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialize);
            var initCustody = Assert.IsType<ContainerOperationCustody.RetainedOperation>(ContainerOperationCustody.CustodyFor(primary));
            // This is the real xUnit fixture's second disposal path, after InitializeAsync already returned failure.
            var disposalFailure = await Assert.ThrowsAsync<TimeoutException>(() => fixture.DisposeAsync());
            var disposalCustody = Assert.IsType<ContainerOperationCustody.RetainedOperation>(ContainerOperationCustody.CustodyFor(disposalFailure));
            Assert.False(actualDatabase.Task.IsCompleted);
            Assert.False(initCustody.Operation.IsCompleted);
            Assert.False(disposalCustody.Operation.IsCompleted);
            Assert.Equal(0, owner.Disposals);
            var late = new IOException("Controlled late fault from the actual fixture database initializer.");
            actualDatabase.SetException(late);
            await Task.WhenAll(initCustody.Observation, disposalCustody.Observation).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(1, owner.Disposals);
            Assert.Contains(late, ContainerOperationCustody.FailuresFor(primary, "maliev.initialization.eventual-failures"));
        }
        finally
        {
            actualDatabase.TrySetResult();
            try { await initialize.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            await fixture.DisposeAsync();
        }
    }

    private sealed class HostileDataException(bool readOnly) : IOException("Controlled hostile exception metadata.")
    {
        private readonly System.Collections.IDictionary immutable =
            new System.Collections.ObjectModel.ReadOnlyDictionary<object, object>(new Dictionary<object, object>());
        public override System.Collections.IDictionary Data => readOnly ? immutable
            : throw new InvalidOperationException("Controlled exception Data getter fault.");
    }

    private sealed class ProbeOwner(Func<Task> dispose) : IAsyncDisposable
    {
        internal int Disposals;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref Disposals); return new(dispose()); }
    }

    private sealed class ProbeResource(Func<CancellationToken, Task> start, Func<Task> dispose) : IStartupResource
    {
        internal int Disposals;
        public Task StartAsync(CancellationToken token) => start(token);
        public Task DisposeAndVerifyAsync() { Interlocked.Increment(ref Disposals); return dispose(); }
    }
}
