using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

// A wait deadline does not prove that the SDK operation ended. Keep its actual task and owners.
internal static class ContainerOperationCustody
{
    private static readonly ConcurrentDictionary<Guid, RetainedOperation> pending = new();
    internal static IReadOnlyCollection<RetainedOperation> Pending => pending.Values.ToArray();
    private static readonly ConditionalWeakTable<Exception, FailureEvidence> evidence = new();
    private sealed class FailureEvidence
    {
        internal readonly object Gate = new();
        internal readonly Dictionary<string, Exception[]> Failures = new();
        internal RetainedOperation? Custody;
    }

    internal sealed class RetainedOperation(Task operation, string phase, object[] owners)
    {
        internal Guid Id { get; } = Guid.NewGuid();
        internal Task Operation { get; } = operation;
        internal string Phase { get; } = phase;
        internal object[] Owners { get; } = owners;
        internal DateTimeOffset ReviewRequiredBy { get; } = DateTimeOffset.UtcNow.AddMinutes(15);
        internal Exception[] EventualFailures { get; set; } = [];
        internal Task Observation { get; set; } = Task.CompletedTask;
    }

    internal static RetainedOperation Retain(Task operation, string phase, object[] owners, Action? release = null)
    {
        var entry = new RetainedOperation(operation, phase, owners);
        pending[entry.Id] = entry;
        entry.Observation = ObserveAsync();
        return entry;

        async Task ObserveAsync()
        {
            try { await operation; }
            catch (Exception failure)
            {
                entry.EventualFailures = operation.Exception?.Flatten().InnerExceptions.ToArray() ?? [failure];
            }
            try { release?.Invoke(); }
            catch (Exception failure)
            {
                entry.EventualFailures = [.. entry.EventualFailures, failure];
                return; // Release failed: retain custody even though the operation settled.
            }
            if (operation.IsCompletedSuccessfully) pending.TryRemove(entry.Id, out _);
            // A settled cleanup fault is still unresolved resource custody, not proof of absence.
        }
    }

    internal static async Task CleanupPreservingAsync(Exception primary, Task cleanup, TimeSpan budget,
        string phase, object[] owners)
    {
        try { await cleanup.WaitAsync(budget); }
        catch (Exception failure)
        {
            // Retain the real operation before attempting optional exception metadata.
            AttachCustody(primary, Retain(cleanup, phase, owners));
            RecordFailures(primary, "maliev.cleanup.failures", cleanup.Exception?.Flatten().InnerExceptions.ToArray() ?? [failure]);
        }
    }

    internal static void RecordFailures(Exception primary, string category, params Exception[] failures)
    {
        var record = evidence.GetValue(primary, _ => new FailureEvidence());
        lock (record.Gate)
        {
            var prior = record.Failures.GetValueOrDefault(category) ?? [];
            var combined = prior.Concat(failures).Distinct().ToArray();
            record.Failures[category] = combined;
            TryMetadata(primary, category, combined, record);
        }
    }

    internal static Exception[] FailuresFor(Exception primary, string category)
    {
        if (!evidence.TryGetValue(primary, out var record)) return [];
        lock (record.Gate) return record.Failures.GetValueOrDefault(category) ?? [];
    }

    internal static RetainedOperation? CustodyFor(Exception primary)
    {
        if (!evidence.TryGetValue(primary, out var record)) return null;
        lock (record.Gate) return record.Custody;
    }

    internal static void AttachCustody(Exception primary, RetainedOperation custody)
    {
        var record = evidence.GetValue(primary, _ => new FailureEvidence());
        lock (record.Gate)
        {
            record.Custody = custody;
            TryMetadata(primary, "maliev.cleanup.custody", custody, record);
        }
    }

    private static void TryMetadata(Exception primary, string category, object value, FailureEvidence record)
    {
        try { primary.Data[category] = value; }
        catch (Exception metadataFailure)
        {
            const string key = "maliev.evidence.metadata.failures";
            record.Failures[key] = [.. (record.Failures.GetValueOrDefault(key) ?? []), metadataFailure];
        }
    }

    internal static async Task CancelPreservingAsync(CancellationTokenSource source, Exception primary,
        TimeSpan budget, Action<Task> track)
    {
        Task? actual = null;
        try
        {
            actual = source.CancelAsync();
            track(actual);
            await actual.WaitAsync(budget);
        }
        catch (Exception failure)
        {
            if (actual is { IsCompleted: false }) Retain(actual, "cancellation-callbacks", [source, primary]);
            RecordFailures(primary, "maliev.cancellation.callback.failures",
                actual?.Exception?.Flatten().InnerExceptions.ToArray() ?? [failure]);
        }
    }

    internal sealed class InitializationLifetime(IAsyncDisposable owner) : IAsyncDisposable
    {
        private readonly object gate = new();
        private Task? operation;
        private Task? disposal;
        internal Task InitializeAsync(Func<CancellationToken, Task> initialize, CancellationToken token,
            TimeSpan? cleanupBudget = null)
        {
            return ContainerOperationCustody.InitializeAsync(this, Start, token, cleanupBudget);

            Task Start(CancellationToken cancellation)
            {
                lock (gate)
                {
                    if (disposal is not null) throw new InvalidOperationException("Initialization owner disposal already started.");
                    return operation ??= initialize(cancellation);
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate) return new(disposal ??= DisposeAfterInitializationAsync());
        }

        private async Task DisposeAfterInitializationAsync()
        {
            if (operation is not null)
            {
                try { await operation; }
                catch { /* The initialization caller retains its failure; do not race its actual operation. */ }
            }
            await owner.DisposeAsync();
        }
    }

    internal static async Task InitializeAsync(IAsyncDisposable owner, Func<CancellationToken, Task> initialize,
        CancellationToken token, TimeSpan? cleanupBudget = null)
    {
        Task? operation = null;
        try
        {
            token.ThrowIfCancellationRequested();
            operation = initialize(token);
            await operation.WaitAsync(token);
        }
        catch (Exception primary)
        {
            var cleanup = CleanupAfterSettlementAsync(primary);
            await CleanupPreservingAsync(primary, cleanup, cleanupBudget ?? TimeSpan.FromSeconds(15),
                "fixture-initialization-cleanup", [owner, operation!]);
            throw;
        }

        async Task CleanupAfterSettlementAsync(Exception primary)
        {
            if (operation is not null)
            {
                try { await operation; }
                catch (Exception failure) { RecordFailures(primary, "maliev.initialization.eventual-failures", failure); }
            }
            await owner.DisposeAsync();
        }
    }
}
