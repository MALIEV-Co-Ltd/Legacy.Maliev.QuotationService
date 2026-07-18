using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationChildCreateIdempotencyTests
{
    [Fact]
    public async Task CreateOrderItem_SameKey_ReplaysOriginalResponseWithoutDuplicate()
    {
        var service = new Mock<IQuotationService>();
        var request = new UpsertQuotationOrderItemRequest(7, 42, "Thai tone mark: น้ำ", 2, 50.25m);
        var created = new QuotationOrderItemResponse(9, 7, 42, request.Description, 2, 50.25m, 100.50m, null, null);
        service.Setup(value => value.CreateOrderItemAsync(request, It.IsAny<CancellationToken>())).ReturnsAsync(created);
        var controller = new OrderItemsController(service.Object, new MemoryIdempotencyStore());

        var first = await controller.CreateOrderItemAsync(request, "attempt:line:0", CancellationToken.None);
        var replay = await controller.CreateOrderItemAsync(request, "attempt:line:0", CancellationToken.None);

        Assert.Equal(9, Assert.IsType<QuotationOrderItemResponse>(Assert.IsType<CreatedAtRouteResult>(first).Value).Id);
        Assert.Equal(9, Assert.IsType<QuotationOrderItemResponse>(Assert.IsType<CreatedAtRouteResult>(replay).Value).Id);
        service.Verify(value => value.CreateOrderItemAsync(request, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateOrderLink_SameKey_ReplaysOriginalResponseWithoutDuplicate()
    {
        var service = new Mock<IQuotationService>();
        var created = new QuotationOrderLinkResponse(11, 7, 42, null, null);
        service.Setup(value => value.CreateOrderLinkAsync(7, 42, It.IsAny<CancellationToken>())).ReturnsAsync(created);
        var controller = new OrdersController(service.Object, new MemoryIdempotencyStore());

        var first = await controller.CreateQuotationOrderLinkAsync(7, 42, "attempt:order:42", CancellationToken.None);
        var replay = await controller.CreateQuotationOrderLinkAsync(7, 42, "attempt:order:42", CancellationToken.None);

        Assert.Equal(11, Assert.IsType<QuotationOrderLinkResponse>(Assert.IsType<CreatedAtRouteResult>(first).Value).Id);
        Assert.Equal(11, Assert.IsType<QuotationOrderLinkResponse>(Assert.IsType<CreatedAtRouteResult>(replay).Value).Id);
        service.Verify(value => value.CreateOrderLinkAsync(7, 42, It.IsAny<CancellationToken>()), Times.Once);
    }

    private sealed class MemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);

        public Task<T?> GetAsync<T>(string scope, string key, CancellationToken cancellationToken) where T : class =>
            Task.FromResult(values.TryGetValue($"{scope}:{key}", out var value) ? (T)value : null);

        public Task SetAsync<T>(string scope, string key, T response, CancellationToken cancellationToken) where T : class
        {
            values[$"{scope}:{key}"] = response;
            return Task.CompletedTask;
        }
    }
}
