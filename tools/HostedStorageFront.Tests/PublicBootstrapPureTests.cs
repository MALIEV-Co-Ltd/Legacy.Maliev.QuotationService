using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvoiceCompletionProducerAcceptance.Companion;

public sealed class PublicBootstrapPureTests
{
    private const string Run = "c821-11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task ExactRsa2048PublicBootstrapIsAcceptedWithoutProcessAuthority()
    {
        var (json, expires) = Frame();
        var actual = await Read(json, expires);
        Assert.Equal("GOOG4-RSA-SHA256", actual.Algorithm);
        Assert.Equal(Run, actual.File.RunId);
        using var publicKey = RSA.Create();
        publicKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(actual.PublicKey), out int consumed);
        Assert.Equal(2048, publicKey.KeySize);
        Assert.Equal(Convert.FromBase64String(actual.PublicKey).Length, consumed);
    }

    [Theory]
    [InlineData("Algorithm")]
    [InlineData("PublicKey")]
    [InlineData("File")]
    public async Task MissingTopLevelFieldIsRejected(string field)
    {
        var (json, expires) = Frame();
        Assert.True(json.Remove(field));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(json, expires));
    }

    [Theory]
    [InlineData("Algorithm")]
    [InlineData("PublicKey")]
    [InlineData("File")]
    public async Task NullRequiredFieldIsRejected(string field)
    {
        var (json, expires) = Frame();
        json[field] = null;
        if (field == "File")
            await Assert.ThrowsAsync<InvalidDataException>(() => Read(json, expires));
        else
            await Assert.ThrowsAsync<JsonException>(() => Read(json, expires));
    }

    [Theory]
    [InlineData("Pid")]
    [InlineData("StartedUtc")]
    [InlineData("ExecutableDll")]
    [InlineData("ExecutableSha256")]
    [InlineData("RunId")]
    [InlineData("ExpiresUtc")]
    public async Task MissingFileIdentityFieldIsRejected(string field)
    {
        var (json, expires) = Frame();
        Assert.True(json["File"]!.AsObject().Remove(field));
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(json, expires));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("file")]
    public async Task DuplicateIdentityFieldIsRejected(string where)
    {
        var (json, expires) = Frame();
        string value = json.ToJsonString();
        value = where == "root" ? value.Insert(1, "\"Algorithm\":\"GOOG4-RSA-SHA256\",")
            : value.Replace("\"Pid\":123", "\"Pid\":123,\"Pid\":123", StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(value, expires));
    }

    [Fact]
    public async Task PrivateOrExtraFieldsAreRejected()
    {
        var (json, expires) = Frame();
        json["PrivateKey"] = "forbidden";
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(json, expires));
    }

    [Fact]
    public async Task ASecondJsonMessageCannotBeBootstrapped()
    {
        var (json, expires) = Frame();
        await Assert.ThrowsAsync<JsonException>(() => Read(json.ToJsonString() + "{}", expires));
    }

    [Fact]
    public async Task OversizedFrameIsRejectedBeforeParsing()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(new string(' ', 16385), DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(31)]
    public async Task ExpiredOrUnboundedLeaseIsRejected(int minutes)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Read("{}", DateTimeOffset.UtcNow.AddMinutes(minutes)));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("expiry")]
    [InlineData("pid")]
    [InlineData("future-start")]
    [InlineData("relative-dll")]
    [InlineData("digest")]
    [InlineData("algorithm")]
    public async Task DeclaredIdentityMismatchIsRejected(string mutation)
    {
        var (json, expires) = Frame();
        var file = json["File"]!.AsObject();
        switch (mutation)
        {
            case "run": file["RunId"] = "other"; break;
            case "expiry": file["ExpiresUtc"] = expires.AddSeconds(1); break;
            case "pid": file["Pid"] = 0; break;
            case "future-start": file["StartedUtc"] = expires.AddSeconds(1); break;
            case "relative-dll": file["ExecutableDll"] = "file.dll"; break;
            case "digest": file["ExecutableSha256"] = new string('g', 64); break;
            case "algorithm": json["Algorithm"] = "GOOG4-HMAC-SHA256"; break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(json, expires));
    }

    [Theory]
    [InlineData(1024)]
    [InlineData(3072)]
    public async Task WrongRsaSizeIsRejected(int bits)
    {
        var (json, expires) = Frame();
        using var key = RSA.Create(bits);
        json["PublicKey"] = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(json, expires));
    }

    [Fact]
    public async Task NoncanonicalBase64IsRejected()
    {
        var (json, expires) = Frame();
        json["PublicKey"] = " " + json["PublicKey"]!.GetValue<string>();
        await Assert.ThrowsAsync<InvalidDataException>(() => Read(json, expires));
    }

    private static (JsonObject Json, DateTimeOffset Expires) Frame()
    {
        var expires = DateTimeOffset.UtcNow.AddMinutes(2);
        using var key = RSA.Create(2048);
        var value = new PublicSigningBootstrap("GOOG4-RSA-SHA256", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()),
            new ObservedFileHost(123, DateTimeOffset.UtcNow.AddMinutes(-1), Path.GetFullPath("file.dll"), new string('a', 64), Run, expires));
        return (JsonSerializer.SerializeToNode(value)!.AsObject(), expires);
    }

    private static Task<PublicSigningBootstrap> Read(JsonObject json, DateTimeOffset expires) => Read(json.ToJsonString(), expires);
    private static async Task<PublicSigningBootstrap> Read(string json, DateTimeOffset expires)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return await PublicSigningBootstrapReader.ReadAsync(stream, Run, expires, CancellationToken.None);
    }
}
