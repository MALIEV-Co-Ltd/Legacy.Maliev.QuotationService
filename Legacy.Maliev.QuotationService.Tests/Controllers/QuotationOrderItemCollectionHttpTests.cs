using System.Net;
using System.Net.Http.Json;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationOrderItemCollectionHttpTests(DraftAggregateFixture fixture) : IClassFixture<DraftAggregateFixture>
{
    [Fact]
    public async Task Authenticated_allowed_zero_parent_keeps_source_wire400_before_service_query()
    {
        var service = Service();
        await using var original = fixture.App();
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IQuotationService>();
            services.AddSingleton(service.Object);
        }));
        using var client = fixture.Client(app);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/quotations/0/orderitems");
        request.Headers.Accept.Add(new("application/json"));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Quotation id is required", await response.Content.ReadFromJsonAsync<string>());
        service.Verify(value => value.GetOrderItemsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task Zero_parent_keeps_authentication_and_live_denial_before_input_validation(bool authenticated, HttpStatusCode expected)
    {
        var service = Service();
        await using var original = fixture.App(allowed: false);
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IQuotationService>();
            services.AddSingleton(service.Object);
        }));
        using var client = fixture.Client(app, authenticated);
        using var response = await client.GetAsync("/quotations/0/orderitems");
        Assert.Equal(expected, response.StatusCode);
        service.Verify(value => value.GetOrderItemsAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Authenticated_allowed_nonzero_empty_collection_retains_http404()
    {
        var service = Service();
        service.Setup(value => value.GetOrderItemsAsync(17, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QuotationOrderItemResponse>());
        await using var original = fixture.App();
        await using var app = original.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IQuotationService>();
            services.AddSingleton(service.Object);
        }));
        using var client = fixture.Client(app);
        using var response = await client.GetAsync("/quotations/17/orderitems");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        service.Verify(value => value.GetOrderItemsAsync(17, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IQuotationService> Service()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        service.Setup(value => value.DeclineExpiredAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        return service;
    }
}
