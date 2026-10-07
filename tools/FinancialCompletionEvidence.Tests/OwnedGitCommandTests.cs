using System.Collections.ObjectModel;
using System.Diagnostics;
using InvoiceCompletionProducerAcceptance;
using Xunit;

namespace FinancialCompletionEvidence.Tests;

public sealed class OwnedGitCommandTests
{
    [Fact]
    public async Task FastCommandRetainsActualChildExitWithoutInventingMissingObservations()
    {
        var resources = new List<GitResource>();
        var output = await OwnedGitCommand.RunAsync(Directory.GetCurrentDirectory(), ["--version"], resources, CancellationToken.None);
        Assert.StartsWith("git version ", output);
        var resource = Assert.Single(resources);
        Assert.True(resource.ProcessId > 0);
        Assert.True(resource.Exited);
        if (resource.ActualStartUtc is { } observed) Assert.Equal(DateTimeKind.Utc, observed.Kind);
        if (resource.Executable is { } executable) Assert.True(Path.IsPathRooted(executable));
    }

    [Fact]
    public async Task OutputBeyondDeclaredBoundFailsAndReapsTheActualChild()
    {
        var resources = new List<GitResource>();
        await Assert.ThrowsAsync<InvalidDataException>(() => OwnedGitCommand.RunAsync(
            Directory.GetCurrentDirectory(), ["--version"], resources, CancellationToken.None, 1));
        Assert.True(Assert.Single(resources).Exited);
    }

    [Fact]
    public async Task CancelledBlockingCommandClosesItsOwnedPipeAndReapsItsChild()
    {
        var resources = new List<GitResource>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OwnedGitCommand.RunAsync(
            Directory.GetCurrentDirectory(), ["hash-object", "--stdin"], resources, deadline.Token));
        Assert.True(Assert.Single(resources).Exited);
    }

    [Fact]
    public async Task RejectingLedgerCannotLeaveItsObservedBlockingChildRunning()
    {
        var ledger = new RejectingLedger();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => OwnedGitCommand.RunAsync(
            Directory.GetCurrentDirectory(), ["hash-object", "--stdin"], ledger, CancellationToken.None));
        var resource = Assert.IsType<GitResource>(failure.Data["OwnedGitResource"]);
        Assert.Equal(resource, ledger.Observed);
        Assert.True(resource.Exited);
        Assert.NotNull(resource.ActualStartUtc);
        try
        {
            using var process = Process.GetProcessById(resource.ProcessId);
            Assert.True(process.HasExited || process.StartTime.ToUniversalTime() != resource.ActualStartUtc);
        }
        catch (ArgumentException) { /* The exact owned process has already been reaped. */ }
    }

    private sealed class RejectingLedger : Collection<GitResource>
    {
        internal GitResource? Observed { get; private set; }

        protected override void InsertItem(int index, GitResource item)
        {
            Observed = item;
            throw new InvalidOperationException("Synthetic ledger admission failure");
        }
    }
}
