using System.Text;

namespace InvoiceCompletionProducerAcceptance.Companion;

// Production decision seams only. These controls do not launch a child or prove physical FD closure.
public sealed class BoundedOwnedCommandLifecycleTests
{
    [Fact]
    public async Task SignalFailureDoesNotSkipOriginalWaitOrEitherReader()
    {
        var attempts = new List<string>();
        var failures = new List<Exception>();
        var signal = new InvalidDataException("controlled signal failure");
        await BoundedOwnedCommand.AttemptIndependentlyAsync([
            () => { attempts.Add("signal"); throw signal; },
            () => { attempts.Add("wait"); return Task.CompletedTask; },
            () => { attempts.Add("stdout"); return Task.CompletedTask; },
            () => { attempts.Add("stderr"); return Task.CompletedTask; },
        ], failures.Add);
        Assert.Equal(new[] { "signal", "wait", "stdout", "stderr" }, attempts);
        Assert.Same(signal, Assert.Single(failures));
    }

    [Fact]
    public async Task IndependentCloseFailuresPreserveBothActualFailureObjects()
    {
        var failures = new List<Exception>();
        var first = new IOException("controlled first close failure");
        var second = new InvalidOperationException("controlled second close failure");
        bool third = false;
        await BoundedOwnedCommand.AttemptIndependentlyAsync([
            () => throw first,
            () => throw second,
            () => { third = true; return Task.CompletedTask; },
        ], failures.Add);
        Assert.True(third);
        Assert.Equal(2, failures.Count);
        Assert.Same(first, failures[0]);
        Assert.Same(second, failures[1]);
    }

    [Fact]
    public void UncertainClosureNeverRetriesTheOriginalRawClose()
    {
        int attempts = 0;
        var failure = new IOException("controlled close ambiguity");
        var close = new BoundedOwnedCommand.CloseOnce(() => { attempts++; throw failure; }, () => false);
        Assert.Same(failure, Assert.Throws<IOException>(close.Attempt));
        Assert.Throws<InvalidDataException>(close.Attempt);
        Assert.Equal(1, attempts);
        Assert.False(close.Completed);
    }

    [Fact]
    public void SuccessfulClosureIsNotRepeatedOnRecovery()
    {
        int attempts = 0;
        var close = new BoundedOwnedCommand.CloseOnce(() => attempts++, () => true);
        close.Attempt();
        close.Attempt();
        Assert.Equal(1, attempts);
        Assert.True(close.Completed);
    }

    [Fact]
    public void ReturnFromCloseWithoutClosedObservationDoesNotAuthorizeRetry()
    {
        int attempts = 0;
        var close = new BoundedOwnedCommand.CloseOnce(() => attempts++, () => false);
        Assert.Throws<InvalidDataException>(close.Attempt);
        Assert.Throws<InvalidDataException>(close.Attempt);
        Assert.Equal(1, attempts);
        Assert.False(close.Completed);
    }

    [Fact]
    public async Task CanceledFiniteObservationRetainsAnUnsettledOriginalTask()
    {
        var original = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var finite = new CancellationTokenSource();
        finite.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            BoundedOwnedCommand.ObserveSettledAsync(original.Task, finite.Token));
        Assert.False(original.Task.IsCompleted);
        Assert.False(BoundedOwnedCommand.ReleaseAllowed(true, true, true, true, [original.Task]));
        original.SetResult();
        await BoundedOwnedCommand.ObserveSettledAsync(original.Task, CancellationToken.None);
        Assert.True(BoundedOwnedCommand.ReleaseAllowed(true, true, true, true, [original.Task]));
    }

    [Fact]
    public async Task CompletedReaderFaultIsObservedButCannotSubstituteForEof()
    {
        var fault = Task.FromException(new IOException("controlled reader failure"));
        await BoundedOwnedCommand.ObserveSettledAsync(fault, CancellationToken.None);
        Assert.False(BoundedOwnedCommand.ReleaseAllowed(true, false, true, true, [fault]));
        Assert.True(fault.IsFaulted);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void EachMissingPhysicalBarrierRefusesRelease(bool exited, bool outputEof, bool errorEof, bool handles)
    {
        Assert.False(BoundedOwnedCommand.ReleaseAllowed(exited, outputEof, errorEof, handles, [Task.CompletedTask]));
    }

    [Fact]
    public void SettledSemanticFailureDoesNotBecomeCleanupUncertainty()
    {
        Assert.True(BoundedOwnedCommand.AdmissionAllowed(cleanupFailed: false, retainedUnsettled: false));
        Assert.False(BoundedOwnedCommand.AdmissionAllowed(cleanupFailed: true, retainedUnsettled: false));
        Assert.False(BoundedOwnedCommand.AdmissionAllowed(cleanupFailed: false, retainedUnsettled: true));
    }

    [Fact]
    public void InitialCleanupAndOneFreshRecoveryNeverAdmitAThirdAttempt()
    {
        Assert.True(BoundedOwnedCommand.RecoveryAllowed(0));
        Assert.True(BoundedOwnedCommand.RecoveryAllowed(1));
        Assert.False(BoundedOwnedCommand.RecoveryAllowed(2));
        Assert.False(BoundedOwnedCommand.RecoveryAllowed(-1));
    }

    [Fact]
    public void KernelGenerationUsesOriginalPidParentAndField22()
    {
        string[] fields = Enumerable.Repeat("0", 20).ToArray();
        fields[0] = "S";
        fields[1] = "42";
        fields[19] = "987654";
        byte[] source = Encoding.ASCII.GetBytes("321 (command (name)) " + string.Join(' ', fields));
        Assert.Equal((987654UL, 42), BoundedOwnedCommand.ParseKernel(source, 321));
        Assert.Throws<InvalidDataException>(() => BoundedOwnedCommand.ParseKernel(source, 322));
        fields[19] = "0";
        Assert.Throws<InvalidDataException>(() => BoundedOwnedCommand.ParseKernel(
            Encoding.ASCII.GetBytes("321 (command) " + string.Join(' ', fields)), 321));
    }

    [Fact]
    public void OverlongAndMalformedKernelMetadataAreRefusedBeforeAssociation()
    {
        Assert.Throws<InvalidDataException>(() => BoundedOwnedCommand.ParseKernel(new byte[4097], 321));
        Assert.Throws<InvalidDataException>(() => BoundedOwnedCommand.ParseKernel(Encoding.ASCII.GetBytes("321 malformed"), 321));
    }

    [Theory]
    [InlineData("Pid:\t321\n", true)]
    [InlineData("Pid:\t322\n", false)]
    [InlineData("Pid:\t-1\n", false)]
    [InlineData("Pid:\t321\nPid:\t321\n", false)]
    [InlineData("missing", false)]
    public void DescriptorPidMustBeUniqueAndMatchTheOriginal(string source, bool expected)
    {
        Assert.Equal(expected, BoundedOwnedCommand.PidfdMatches(Encoding.ASCII.GetBytes(source), 321));
        Assert.False(BoundedOwnedCommand.PidfdMatches(new byte[1025], 321));
    }
}
