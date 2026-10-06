using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvoiceCompletionProducerAcceptance.Companion;

public sealed class FrontAdmissionPureTests
{
    [Fact]
    public void ExactPublicProfileRoundTripsWithoutActivatingResources()
    {
        var profile = Profile();
        var parsed = FrontHostAdmission.Parse(JsonSerializer.SerializeToUtf8Bytes(profile));
        Assert.Equal(profile.RunId, parsed.RunId);
        Assert.Equal(profile.Backend.ContainerId, parsed.Backend.ContainerId);
        Assert.Equal(profile.BootstrapPipeInode, parsed.BootstrapPipeInode);
    }

    [Theory]
    [InlineData("RunId")]
    [InlineData("Backend")]
    [InlineData("BootstrapPipeInode")]
    public void MissingRequiredProfileMemberIsRejected(string field)
    {
        var json = JsonSerializer.SerializeToNode(Profile())!.AsObject();
        Assert.True(json.Remove(field));
        Assert.Throws<JsonException>(() => FrontHostAdmission.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData("RunId")]
    [InlineData("Backend")]
    [InlineData("ParentScriptPath")]
    public void NullRequiredProfileMemberIsRejected(string field)
    {
        var json = JsonSerializer.SerializeToNode(Profile())!.AsObject();
        json[field] = null;
        Assert.Throws<JsonException>(() => FrontHostAdmission.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Fact]
    public void UnrecognizedProfileFieldIsRejected()
    {
        var json = JsonSerializer.SerializeToNode(Profile())!.AsObject();
        json["PrivateKey"] = "forbidden";
        Assert.Throws<JsonException>(() => FrontHostAdmission.Parse(Encoding.UTF8.GetBytes(json.ToJsonString())));
    }

    [Theory]
    [InlineData("{\"RunId\":1,\"RunId\":2}")]
    [InlineData("{\"Backend\":{\"Port\":1,\"Port\":2}}")]
    [InlineData("[{\"Port\":1,\"Port\":2}]")]
    public void DuplicateFieldsAreRejectedRecursively(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Throws<InvalidDataException>(() => FrontHostAdmission.UniqueObjects(document.RootElement));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32769)]
    public void ProfileByteBoundIsEnforcedBeforeParsing(int count)
    {
        Assert.Throws<InvalidDataException>(() => FrontHostAdmission.Parse(new byte[count]));
    }

    [Fact]
    public void KernelProcessNameCanContainSpacesAndClosingParentheses()
    {
        var actual = FrontHostAdmission.ProcessStat(Stat("123 (owner (worker))", "S", "42", "98765"));
        Assert.Equal(42, actual.Parent);
        Assert.Equal(98765L, actual.Start);
    }

    [Theory]
    [InlineData("Z", "42", "98765")]
    [InlineData("X", "42", "98765")]
    [InlineData("x", "42", "98765")]
    [InlineData("S", "0", "98765")]
    [InlineData("S", "42", "0")]
    [InlineData("S", "42", "-1")]
    public void MissingOrDeadOwnerGenerationIsRejected(string state, string parent, string ticks)
    {
        Assert.Throws<InvalidDataException>(() => FrontHostAdmission.ProcessStat(Stat("123 (owner)", state, parent, ticks)));
    }

    [Theory]
    [InlineData("0 (owner) S 42")]
    [InlineData("123 owner S 42")]
    [InlineData("123 (owner) S 42")]
    public void MalformedKernelObservationCannotEstablishOwnership(string stat)
    {
        Assert.Throws<InvalidDataException>(() => FrontHostAdmission.ProcessStat(stat));
    }

    internal static string Stat(string prefix, string state, string parent, string ticks) =>
        prefix + " " + state + " " + parent + " " + string.Join(" ", Enumerable.Repeat("0", 17)) + " " + ticks;

    internal static FrontHostProfile Profile() => new("c821-11111111-2222-3333-4444-555555555555",
        new DateTimeOffset(2026, 10, 6, 0, 20, 0, TimeSpan.Zero), new Uri("http://127.0.0.1:45001/"),
        BackendObservationPureTests.Lease(), "/workspace", new string('a', 40), "/workspace/front.dll", new string('b', 64),
        42, 98765, "/usr/bin/python3", new string('c', 64), "/workspace/owner.py", new string('d', 64), "5", "12345");
}
