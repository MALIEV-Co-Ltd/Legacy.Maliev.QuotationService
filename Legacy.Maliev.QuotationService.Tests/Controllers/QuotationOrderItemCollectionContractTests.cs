using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationOrderItemCollectionContractTests
{
    [Fact]
    public async Task Zero_parent_returns_original_bad_request_without_querying_service()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        var idempotency = new Mock<IIdempotencyStore>(MockBehavior.Strict);
        var controller = new OrderItemsController(service.Object, idempotency.Object);
        var response = await controller.GetOrderItemsAsync(0, CancellationToken.None);
        Assert.Equal("Quotation id is required", Assert.IsType<BadRequestObjectResult>(response.Result).Value);
        service.VerifyNoOtherCalls();
        idempotency.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(17)]
    [InlineData(-1)]
    public async Task Nonzero_empty_collection_keeps_original_notfound(int quotationId)
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        service.Setup(value => value.GetOrderItemsAsync(quotationId, cancellation.Token))
            .ReturnsAsync(Array.Empty<QuotationOrderItemResponse>());
        var idempotency = new Mock<IIdempotencyStore>(MockBehavior.Strict);
        var controller = new OrderItemsController(service.Object, idempotency.Object);
        var response = await controller.GetOrderItemsAsync(quotationId, cancellation.Token);
        Assert.IsType<NotFoundResult>(response.Result);
        service.Verify(value => value.GetOrderItemsAsync(quotationId, cancellation.Token), Times.Once);
        service.VerifyNoOtherCalls();
        idempotency.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Nonzero_collection_keeps_service_payload_and_exact_parent_token()
    {
        IReadOnlyList<QuotationOrderItemResponse> items =
            [new(3, 17, 901, "synthetic line", 2, 15.25m, 30.50m, null, null)];
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        using var cancellation = new CancellationTokenSource();
        service.Setup(value => value.GetOrderItemsAsync(17, cancellation.Token)).ReturnsAsync(items);
        var idempotency = new Mock<IIdempotencyStore>(MockBehavior.Strict);
        var controller = new OrderItemsController(service.Object, idempotency.Object);
        var response = await controller.GetOrderItemsAsync(17, cancellation.Token);
        Assert.Same(items, Assert.IsType<OkObjectResult>(response.Result).Value);
        service.Verify(value => value.GetOrderItemsAsync(17, cancellation.Token), Times.Once);
        service.VerifyNoOtherCalls();
        idempotency.VerifyNoOtherCalls();
    }
}
