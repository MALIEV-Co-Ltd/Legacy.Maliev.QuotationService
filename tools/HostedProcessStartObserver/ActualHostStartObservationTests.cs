using System.Text;
using HostedProcessStartObserver;
using Xunit;

public sealed class ActualHostStartObservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    private static HostStartRequest Request() => new("Quotation", "c821-11111111-1111-4111-8111-111111111111", Now.AddMinutes(10),
        "123", "1", new(10, 20, "/runtime/python", new('A', 64), "/source/parent.py", new('B', 64)),
        30, 40, "/runtime/dotnet", new('C', 64), "/source/Api.dll", new('D', 64), new('E', 64));

    [Fact]
    public void ActualUtcValueIsPreservedWithoutRoundingOrEventTime()
    {
        var started = Now.UtcDateTime.AddSeconds(-5).AddTicks(1234);
        ActualHostStartObservation.ValidateReobservations(Request(), new(30, 10, 40), new(30, 10, 40), started, started, Now);
        var evidence = new HostStartObservation("Quotation", 30, new(started), 40, "/runtime/dotnet", new('C', 64), "/source/Api.dll", new('D', 64));
        Assert.Equal(started.Ticks, evidence.StartedUtc.UtcTicks);
    }

    [Fact]
    public void ForeignOrChangedTargetKernelIdentityRejected()
    {
        var started = Now.UtcDateTime.AddSeconds(-5);
        foreach (var wrong in new[] { new ActualHostStartObservation.KernelIdentity(31, 10, 40), new(30, 11, 40), new(30, 10, 41) })
            Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateReobservations(Request(), new(30, 10, 40), wrong, started, started, Now));
    }

    [Fact]
    public void ChangedStartTimeAndNonUtcStartRejected()
    {
        var started = Now.UtcDateTime.AddSeconds(-5);
        Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateReobservations(Request(), new(30, 10, 40), new(30, 10, 40), started, started.AddTicks(1), Now));
        Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateReobservations(Request(), new(30, 10, 40), new(30, 10, 40), DateTime.SpecifyKind(started, DateTimeKind.Unspecified), started, Now));
    }

    [Fact]
    public void FutureStartAndExpiredLeaseRejected()
    {
        Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateReobservations(Request(), new(30, 10, 40), new(30, 10, 40), Now.UtcDateTime.AddSeconds(1), Now.UtcDateTime.AddSeconds(1), Now));
        Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateReobservations(Request() with { ExpiresUtc = Now }, new(30, 10, 40), new(30, 10, 40), Now.UtcDateTime.AddSeconds(-1), Now.UtcDateTime.AddSeconds(-1), Now));
    }

    [Fact]
    public void KernelParserRetainsGenerationDespiteCommParentheses()
    {
        var tail = new[] { "S", "10" }.Concat(Enumerable.Repeat("0", 17)).Concat(["40"]);
        Assert.Equal(new ActualHostStartObservation.KernelIdentity(30, 10, 40),
            ActualHostStartObservation.ParseStat(Encoding.ASCII.GetBytes("30 (normal (host) name) " + string.Join(' ', tail)), 30));
    }

    [Fact]
    public void KernelParserRejectsWrongPidDeadAndTruncatedStat()
    {
        foreach (var text in new[] { "31 (host) S 10", "30 (host) Z 10 " + string.Join(' ', Enumerable.Repeat("40", 18)), "30 (host) S 10" })
            Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ParseStat(Encoding.ASCII.GetBytes(text), 30));
    }

    [Fact]
    public void FullEnvironmentFingerprintIgnoresOrderingButDetectsAnyValueChange()
    {
        var first = new Dictionary<string, string> { ["B"] = "two", ["A"] = "one" };
        var second = new Dictionary<string, string> { ["A"] = "one", ["B"] = "two" };
        Assert.Equal(ActualHostStartObservation.EnvironmentDigest(first), ActualHostStartObservation.EnvironmentDigest(second));
        second["B"] = "changed";
        Assert.NotEqual(ActualHostStartObservation.EnvironmentDigest(first), ActualHostStartObservation.EnvironmentDigest(second));
    }

    [Fact]
    public void RepeatedEnvironmentKeysAndMalformedEntriesRejected()
    {
        foreach (var value in new[] { "A=one\0A=two\0", "=value\0", "missing-equals\0" })
            Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.DecodeEnvironment(Encoding.UTF8.GetBytes(value)));
    }

    [Fact]
    public void InvalidRoleParentPidOrLeaseRejectedBeforeResourceRead()
    {
        foreach (var request in new[] { Request() with { Owner="Synthetic" }, Request() with { ExpiresUtc=Now },
            Request() with { Parent=Request().Parent with { Pid=30 } }, Request() with { KernelStartTicks=0 } })
            Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateRequest(request, Now));
    }

    [Fact]
    public void PublicObservationContainsOnlyIdentityStartPathsAndHashes()
    {
        string[] expected = ["Owner", "Pid", "StartedUtc", "KernelStartTicks", "Executable", "ExecutableSha256", "ExecutableDll", "DllSha256"];
        Assert.Equal(expected.Order(StringComparer.Ordinal), typeof(HostStartObservation).GetProperties().Select(value => value.Name).Order(StringComparer.Ordinal));
    }
}
