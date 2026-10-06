using System.ComponentModel;
using QualificationOutcomeWireSource;

namespace Legacy.Maliev.QuotationService.Tests.Tools;

public sealed class ChildStartObservationTests
{
    [Fact]
    public void LiveChildRetainsObservedGeneration()
    {
        var started = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(started, ChildStartObservation.Capture(() => started, () => false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PositivelyExitedChildAllowsMissingStartObservation(bool invalidOperation)
    {
        Assert.Null(ChildStartObservation.Capture(
            () => throw Failure(invalidOperation), () => true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnreadableLiveChildStillFailsClosed(bool invalidOperation)
    {
        var failure = Failure(invalidOperation);
        var observed = Assert.ThrowsAny<Exception>(() => ChildStartObservation.Capture(
            () => throw failure, () => false));
        Assert.Same(failure, observed);
    }

    private static Exception Failure(bool invalidOperation) => invalidOperation
        ? new InvalidOperationException("Synthetic exited-child observation")
        : new Win32Exception(2);
}
