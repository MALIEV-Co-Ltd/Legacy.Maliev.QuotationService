using System.Net;
using Xunit;

namespace InvoiceCompletionProducerAcceptance.Companion;

// Authored, uncompiled controls. Actual SDK/backend compatibility remains a separate obligation.
public sealed class PreparedResumablePureTests
{
    private const string Upload = "/upload/storage/v1/b/maliev.com/o";
    private static readonly Uri Backend = new("http://172.18.0.2:4443/");
    private static readonly Uri Origin = new("http://127.0.0.1:45001/");

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public void SuccessfulInitiationRewritesObservedBackendSession(int status)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)status);
        response.Headers.Location = new Uri(Backend, Upload + "?upload_id=synthetic-session");
        var result = Read(response, "POST");
        Assert.Equal("http://127.0.0.1:45001" + Upload + "?upload_id=synthetic-session", result.Location);
        Assert.Null(result.Range);
    }

    [Fact]
    public void IncompleteChunkPreservesRangeWithoutLocation()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)308);
        response.Headers.TryAddWithoutValidation("Range", "bytes=0-262143");
        var result = Read(response, "PUT");
        Assert.Equal("bytes=0-262143", result.Range);
        Assert.Null(result.Location);
    }

    [Fact]
    public void EmptyStatusProbeNeedsNoLocationOrRange()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)308);
        Assert.Equal(new ResumableReply(null, null), Read(response, "PUT"));
    }

    [Fact]
    public void SuccessfulSessionCannotEscapeOwnedBackend()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.Location = new Uri("http://127.0.0.1:9999" + Upload + "?upload_id=synthetic");
        Assert.Throws<InvalidDataException>(() => Read(response, "POST"));
    }

    [Fact]
    public void OrdinaryRedirectIsRejected()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        Assert.Throws<InvalidDataException>(() => Read(response, "PUT"));
    }

    [Theory]
    [InlineData("bytes=0-33554432")]
    [InlineData("bytes=2-7")]
    [InlineData("bytes=0--1")]
    public void MalformedOrOversizedAcknowledgementIsRejected(string range)
    {
        using var response = new HttpResponseMessage((HttpStatusCode)308);
        response.Headers.TryAddWithoutValidation("Range", range);
        Assert.Throws<InvalidDataException>(() => Read(response, "PUT"));
    }

    [Fact]
    public void IncompleteResponseCannotGrantAnUnexpectedLocation()
    {
        using var response = new HttpResponseMessage((HttpStatusCode)308);
        response.Headers.Location = new Uri(Backend, Upload + "?upload_id=synthetic");
        Assert.Throws<InvalidDataException>(() => Read(response, "PUT"));
    }

    private static ResumableReply Read(HttpResponseMessage response, string method) =>
        ResumableResponsePolicy.Read(response, true, method, Upload + "?upload_id=synthetic", Backend, Origin, 32 * 1024 * 1024);
}
