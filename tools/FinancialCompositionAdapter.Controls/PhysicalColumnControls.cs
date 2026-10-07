using System.Runtime.ExceptionServices;
using FinancialCompositionAdapter;

namespace FinancialCompositionAdapterControls;

// Pure controls only. These never construct a context, open PostgreSQL or start a host.
public static class PhysicalColumnControls
{
    public static async Task<IReadOnlyList<string>> RunAsync()
    {
        var passed = new List<string>();
        var column = new PhysicalColumnExpectation("amount", "numeric(18,2)", "numeric(18,2)",
            false, "", "", null);
        OwnedPhysicalColumns.VerifyColumn(column, "numeric(18,2)", true, "", "", null);
        passed.Add("ExactColumnMatches");
        Refuses(() => OwnedPhysicalColumns.VerifyColumn(column, "numeric(18,3)", true, "", "", null));
        passed.Add("PrecisionMismatchRefused");
        Refuses(() => OwnedPhysicalColumns.VerifyColumn(column, "numeric(18,2)", false, "", "", null));
        passed.Add("NullabilityMismatchRefused");
        Refuses(() => OwnedPhysicalColumns.VerifyColumn(column, "numeric(18,2)", true, "", "", "0"));
        passed.Add("UnexpectedDefaultRefused");
        Refuses(() => OwnedPhysicalColumns.VerifyColumn(column, "numeric(18,2)", true, "d", "", null));
        passed.Add("IdentityMismatchRefused");
        var computed = column with { GeneratedKind = "s", PostgreSqlExpression = "(quantity * unit_price)" };
        OwnedPhysicalColumns.VerifyColumn(computed, "numeric(18,2)", true, "", "s", "(quantity * unit_price)");
        passed.Add("ExactStoredComputedMatches");
        Refuses(() => OwnedPhysicalColumns.VerifyColumn(computed, "numeric(18,2)", true, "", "v", "(quantity * unit_price)"));
        passed.Add("ComputedStorageMismatchRefused");
        Refuses(() => OwnedPhysicalColumns.VerifyColumn(computed, "numeric(18,2)", true, "", "s", "(quantity + unit_price)"));
        passed.Add("ComputedExpressionMismatchRefused");
        var first = new PhysicalTableExpectation("public", "a", [column]);
        var second = new PhysicalTableExpectation("public", "b", [computed]);
        if (OwnedPhysicalColumns.ComputeExpectationDigest([first, second])
            != OwnedPhysicalColumns.ComputeExpectationDigest([second, first]))
        {
            throw new InvalidDataException("Expectation digest ordering differs.");
        }
        passed.Add("ExpectationDigestOrderStable");
        Refuses(() => OwnedPhysicalColumns.ComputeExpectationDigest([first with { Schema = new string('x', 64) }]));
        passed.Add("OversizedNameRefusedBeforeDigest");
        Refuses(() => OwnedPhysicalColumns.ComputeExpectationDigest([
            first with { Columns = [column with { PostgreSqlExpression = new string('x', 8193) }] }]));
        passed.Add("OversizedExpressionRefusedBeforeDigest");
        var bodyFailure = new InvalidDataException("Synthetic operation failure");
        var releaseFailure = new InvalidDataException("Synthetic release failure");
        var order = new List<int>();
        try
        {
            await PhysicalSchemaRelease.FinishAsync(ExceptionDispatchInfo.Capture(bodyFailure),
            [
                () =>
                {
                    order.Add(1);
                    throw releaseFailure;
                },
                () =>
                {
                    order.Add(2);
                    return Task.CompletedTask;
                },
            ]);
            throw new InvalidDataException("Operation failure was lost.");
        }
        catch (AggregateException failure) when (failure.InnerExceptions.Count == 2
            && ReferenceEquals(failure.InnerExceptions[0], bodyFailure)
            && ReferenceEquals(failure.InnerExceptions[1], releaseFailure)
            && order.SequenceEqual([1, 2]))
        {
            passed.Add("FirstOperationFailureRetainedAndLaterReleaseAttempted");
        }
        try
        {
            await PhysicalSchemaRelease.FinishAsync(null, [() => Task.FromException(releaseFailure)]);
            throw new InvalidDataException("Release failure was lost.");
        }
        catch (InvalidDataException failure) when (ReferenceEquals(failure, releaseFailure))
        {
            passed.Add("SingleReleaseFailureRetained");
        }
        return passed.AsReadOnly();
    }

    private static void Refuses(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidDataException("Column mismatch was not refused.");
    }
}
