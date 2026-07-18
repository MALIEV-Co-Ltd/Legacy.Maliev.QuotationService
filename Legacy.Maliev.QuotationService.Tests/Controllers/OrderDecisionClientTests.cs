using Legacy.Maliev.QuotationService.Api.Clients;
using Legacy.Maliev.QuotationService.Application.Models;
using System.Net;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class OrderDecisionClientTests
{
    [Theory]
    [InlineData(true, "accepted")]
    [InlineData(false, "declined")]
    public async Task TransitionAsync_UsesNamedRouteAndIdempotencyKey(bool accepted, string route)
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://orders.test") };
        var client = new OrderDecisionClient(http);

        var result = await client.TransitionAsync(42, accepted, "quotation-key", CancellationToken.None);

        Assert.Equal(OrderDecisionResult.Completed, result);
        Assert.Equal($"/orderstatuses/histories/42/{route}", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("quotation-key", Assert.Single(captured.Headers.GetValues("Idempotency-Key")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, OrderDecisionResult.Conflict)]
    [InlineData(HttpStatusCode.NotFound, OrderDecisionResult.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, OrderDecisionResult.Unavailable)]
    public async Task TransitionAsync_MapsDependencyResponse(HttpStatusCode status, OrderDecisionResult expected)
    {
        using var http = new HttpClient(new StubHandler(_ => new HttpResponseMessage(status)))
        { BaseAddress = new Uri("https://orders.test") };

        Assert.Equal(expected, await new OrderDecisionClient(http).TransitionAsync(1, true, "key", CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
