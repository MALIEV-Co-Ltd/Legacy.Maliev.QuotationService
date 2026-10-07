using HostedProcessStartObserver;
using Xunit;

public sealed class FrontStartObservationTests
{
    private static HostStartRequest Request(string owner = "Front") => new(owner,
        "c821-11111111-1111-4111-8111-111111111111", DateTimeOffset.UtcNow.AddMinutes(5), "123", "1",
        new(10, 20, "/runtime/python", new('A', 64), "/source/parent.py", new('B', 64)),
        30, 40, "/runtime/dotnet", new('C', 64), "/source/Front.dll", new('D', 64), new('E', 64),
        owner == "Front" ? "/source/TestResults/C821ProducerProfiles/front.json" : "");

    [Fact]
    public void FrontUsesOnlyExactThirdProfileArgument()
    {
        var request = Request();
        ActualHostStartObservation.ValidateCommand(request,
            [request.DotnetExecutable, request.ExecutableDll, request.FrontProfilePath]);
        foreach (var wrong in new string[][]
        {
            [request.DotnetExecutable, request.ExecutableDll],
            [request.DotnetExecutable, request.ExecutableDll, "/source/other.json"],
            [request.DotnetExecutable, request.ExecutableDll, request.FrontProfilePath, "--urls", "http://localhost:1"],
            ["/other/dotnet", request.ExecutableDll, request.FrontProfilePath],
        })
            Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateCommand(request, wrong));
    }

    [Fact]
    public void NormalHostsRetainTwoArgumentContract()
    {
        foreach (var owner in new[] { "Auth", "Accounting", "Quotation", "Order", "IAM", "Document", "File", "Notification" })
        {
            var request = Request(owner);
            ActualHostStartObservation.ValidateCommand(request, [request.DotnetExecutable, request.ExecutableDll]);
            Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateCommand(request,
                [request.DotnetExecutable, request.ExecutableDll, "/source/front.json"]));
            Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateCommand(
                request with { FrontProfilePath = "/source/front.json" }, [request.DotnetExecutable, request.ExecutableDll]));
        }
    }

    [Fact]
    public void FrontCannotOmitItsProfile()
    {
        Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateCommand(
            Request() with { FrontProfilePath = "" }, ["/runtime/dotnet", "/source/Front.dll", ""]));
    }

    [Fact]
    public void FrontPreservesActualUtcAndKernelGenerationChecks()
    {
        var request = Request();
        var now = DateTimeOffset.UtcNow;
        var started = now.UtcDateTime.AddSeconds(-1).AddTicks(1234);
        ActualHostStartObservation.ValidateReobservations(request, new(30, 10, 40), new(30, 10, 40), started, started, now);
        Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateReobservations(
            request, new(30, 10, 40), new(30, 10, 41), started, started, now));
        Assert.Throws<InvalidDataException>(() => ActualHostStartObservation.ValidateReobservations(
            request, new(30, 10, 40), new(30, 10, 40), started, started.AddTicks(1), now));
    }
}
