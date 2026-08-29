using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Application.Services;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Application;

public sealed class QuotationDecisionWorkflowTests
{
    private static readonly DateTime InitialVersion = new(2026, 7, 18, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DecisionVersion = new(2026, 7, 18, 8, 5, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DecideAsync_Accepted_UpdatesQuotationThenTransitionsEveryLinkedOrder()
    {
        var quotations = new Mock<IQuotationService>(MockBehavior.Strict);
        quotations.Setup(value => value.ApplyDecisionAsync(
                7,
                true,
                QuotationAcceptanceOrigin.Employee,
                new DateTimeOffset(InitialVersion),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotationDecisionPersistenceResult(
                QuotationDecisionPersistenceStatus.Completed,
                Quotation(true, DecisionVersion)));
        quotations.Setup(value => value.GetOrderLinksAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Link(11), Link(12)]);
        var orders = new Mock<IOrderDecisionClient>(MockBehavior.Strict);
        orders.Setup(value => value.TransitionAsync(
                It.IsAny<int>(),
                true,
                It.Is<string>(key => key.Contains("quotation-7-accepted-", StringComparison.Ordinal)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrderDecisionResult.Completed);
        var workflow = new QuotationDecisionWorkflow(quotations.Object, orders.Object);

        var result = await workflow.DecideAsync(
            7,
            new QuotationDecisionRequest(true, EmployeeInitiated: true),
            new DateTimeOffset(InitialVersion),
            CancellationToken.None);

        Assert.Equal(QuotationDecisionStatus.Completed, result.Status);
        Assert.Equal(2, result.CompletedOrders);
        Assert.Equal(2, result.TotalOrders);
        Assert.Equal(DecisionVersion, result.ModifiedDate);
        orders.Verify(value => value.TransitionAsync(11, true, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        orders.Verify(value => value.TransitionAsync(12, true, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DecideAsync_AlreadyDesired_ResumesWithSameDeterministicOrderKey()
    {
        var quotations = new Mock<IQuotationService>(MockBehavior.Strict);
        quotations.Setup(value => value.ApplyDecisionAsync(
                7,
                false,
                null,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotationDecisionPersistenceResult(
                QuotationDecisionPersistenceStatus.Completed,
                Quotation(false, DecisionVersion)));
        quotations.Setup(value => value.GetOrderLinksAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Link(11)]);
        string? firstKey = null;
        var orders = new Mock<IOrderDecisionClient>();
        orders.Setup(value => value.TransitionAsync(11, false, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<int, bool, string, CancellationToken>((_, _, key, _) => firstKey ??= key)
            .ReturnsAsync(OrderDecisionResult.Completed);
        var workflow = new QuotationDecisionWorkflow(quotations.Object, orders.Object);

        var first = await workflow.DecideAsync(7, new(false), null, CancellationToken.None);
        var second = await workflow.DecideAsync(7, new(false), null, CancellationToken.None);

        Assert.Equal(QuotationDecisionStatus.Completed, first.Status);
        Assert.Equal(QuotationDecisionStatus.Completed, second.Status);
        orders.Verify(value => value.TransitionAsync(11, false, firstKey!, It.IsAny<CancellationToken>()), Times.Exactly(2));
        quotations.Verify(value => value.ApplyDecisionAsync(
            7, false, null, null, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task DecideAsync_OptimisticConflict_DoesNotTransitionOrders()
    {
        var quotations = new Mock<IQuotationService>();
        quotations.Setup(value => value.ApplyDecisionAsync(
                7,
                true,
                QuotationAcceptanceOrigin.Customer,
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotationDecisionPersistenceResult(
                QuotationDecisionPersistenceStatus.Conflict,
                null));
        var orders = new Mock<IOrderDecisionClient>(MockBehavior.Strict);
        var workflow = new QuotationDecisionWorkflow(quotations.Object, orders.Object);

        var result = await workflow.DecideAsync(7, new(true), new DateTimeOffset(InitialVersion.AddMinutes(-1)), CancellationToken.None);

        Assert.Equal(QuotationDecisionStatus.Conflict, result.Status);
        orders.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DecideAsync_DependencyUnavailable_ReturnsRetryablePartialProgress()
    {
        var quotations = new Mock<IQuotationService>();
        quotations.Setup(value => value.ApplyDecisionAsync(
                7,
                true,
                QuotationAcceptanceOrigin.Customer,
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotationDecisionPersistenceResult(
                QuotationDecisionPersistenceStatus.Completed,
                Quotation(true, DecisionVersion)));
        quotations.Setup(value => value.GetOrderLinksAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Link(11), Link(12)]);
        var orders = new Mock<IOrderDecisionClient>();
        orders.SetupSequence(value => value.TransitionAsync(It.IsAny<int>(), true, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OrderDecisionResult.Completed)
            .ReturnsAsync(OrderDecisionResult.Unavailable);
        var workflow = new QuotationDecisionWorkflow(quotations.Object, orders.Object);

        var result = await workflow.DecideAsync(7, new(true), null, CancellationToken.None);

        Assert.Equal(QuotationDecisionStatus.DependencyUnavailable, result.Status);
        Assert.Equal(1, result.CompletedOrders);
        Assert.Equal(2, result.TotalOrders);
    }

    private static QuotationResponse Quotation(bool? accepted, DateTime modified) => new(
        7, 42, 3, null, 30, new DateTime(2026, 8, 1), 100, 7, 107, 3, 104, 764,
        "comment", "FOB", "carrier", "terms", accepted, InitialVersion, modified);

    private static QuotationOrderLinkResponse Link(int orderId) => new(orderId, 7, orderId, InitialVersion, InitialVersion);
}
