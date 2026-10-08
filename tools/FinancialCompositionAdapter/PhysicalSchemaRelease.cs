using System.Runtime.ExceptionServices;

namespace FinancialCompositionAdapter;

// Retained synchronous invocation order; every returned task is awaited to settlement.
// No timeout abandons a disposal task. The owning parent must supervise a stalled cleanup.
public static class PhysicalSchemaRelease
{
    public static async Task FinishAsync(ExceptionDispatchInfo? operationFailure,
        IReadOnlyList<Func<Task>> releases)
    {
        if (releases.Count is < 1 or > 8)
        {
            throw new InvalidDataException("Finite owned release sequence required.");
        }
        var retained = releases.ToArray();
        var failures = new List<ExceptionDispatchInfo>();
        if (operationFailure is not null)
        {
            failures.Add(operationFailure);
        }
        foreach (var release in retained)
        {
            try
            {
                await release();
            }
            catch (Exception failure)
            {
                failures.Add(ExceptionDispatchInfo.Capture(failure));
            }
        }
        if (failures.Count == 1)
        {
            failures[0].Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException("Physical observation or independent release failed.",
                failures.Select(failure => failure.SourceException));
        }
    }
}
