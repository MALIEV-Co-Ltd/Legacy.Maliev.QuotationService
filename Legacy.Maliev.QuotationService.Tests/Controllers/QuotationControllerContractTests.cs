using System.Reflection;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationControllerContractTests
{
    public static TheoryData<Type, string> Controllers => new()
    {
        { typeof(QuotationsController), "[controller]" }, { typeof(OrderItemsController), "quotations/[controller]" },
        { typeof(OrdersController), "quotations/[controller]" }, { typeof(QuotationFilesController), "quotations/files" },
        { typeof(QuotationRequestsController), "[controller]" }, { typeof(QuotationRequestFilesController), "quotationrequests/files" },
    };

    [Theory, MemberData(nameof(Controllers))]
    public void Controllers_PreserveBaseRoutesAndRequireAuthentication(Type controller, string route)
    { Assert.Equal(route, controller.GetCustomAttribute<RouteAttribute>()?.Template); Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>()); }

    [Fact]
    public void Controllers_PreserveThirtyFourActionsAndThirtyFiveRouteTemplates()
    {
        var methods = Controllers.SelectMany(row => ((Type)row[0]).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)).ToArray();
        Assert.Equal(34, methods.Length);
        Assert.Equal(35, methods.SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()).Count());
        Assert.All(methods, method => Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>()));
    }

    [Fact]
    public void DecisionBoundary_IsCriticalAndUsesQuotationUpdatePermission()
    {
        var action = typeof(QuotationsController).GetMethod(nameof(QuotationsController.DecideQuotationAsync))!;
        Assert.Equal("{quotationId:int}/decision", Assert.Single(action.GetCustomAttributes<HttpPutAttribute>()).Template);
        var permission = Assert.Single(action.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal("legacy.quotations.update", permission.Permission);
        Assert.True(permission.RequireLiveCheck);
        Assert.True(permission.IsCritical);
        Assert.Equal("/quotations/{quotationId}", permission.ResourcePathTemplate);
    }

    [Fact]
    public void CustomerQuotationBoundary_PreservesRoutesAndUsesLeastPrivilegeRead()
    {
        var list = typeof(QuotationsController).GetMethod(nameof(QuotationsController.GetPaginatedQuotationAsync))!;
        Assert.Equal(
            new string?[] { null, "customers/{customerId:int}" },
            list.GetCustomAttributes<HttpGetAttribute>().Select(value => value.Template));
        Assert.Equal(
            "legacy.customer-quotations.read",
            Assert.Single(list.GetCustomAttributes<RequirePermissionAttribute>()).Permission);

        var detail = typeof(QuotationsController).GetMethod(nameof(QuotationsController.GetQuotationAsync))!;
        Assert.Equal("{quotationId:int}", Assert.Single(detail.GetCustomAttributes<HttpGetAttribute>()).Template);
        Assert.Equal(
            "legacy.customer-quotations.read",
            Assert.Single(detail.GetCustomAttributes<RequirePermissionAttribute>()).Permission);
        Assert.Contains(detail.GetParameters(), parameter => parameter.Name == "customerId");
    }

    [Theory]
    [InlineData(typeof(OrderItemsController), nameof(OrderItemsController.GetOrderItemsAsync), "/quotations/{quotationId:int}/orderitems")]
    [InlineData(typeof(OrdersController), nameof(OrdersController.CreateQuotationOrderLinkAsync), "/quotations/{quotationId:int}/orders/{orderId:int}")]
    [InlineData(typeof(OrdersController), nameof(OrdersController.GetAllOrdersFromQuotationAsync), "/quotations/{quotationId:int}/orders")]
    [InlineData(typeof(QuotationFilesController), nameof(QuotationFilesController.GetQuotationFilesAsync), "/quotations/{quotationId:int}/files")]
    [InlineData(typeof(QuotationRequestFilesController), nameof(QuotationRequestFilesController.GetQuotationRequestFilesAsync), "/quotationrequests/{requestId:int}/files")]
    public void CrossResourceRoutes_PreserveLegacyTemplates(Type controller, string action, string expected) =>
        Assert.Equal(expected, Assert.Single(controller.GetMethod(action)!.GetCustomAttributes<HttpMethodAttribute>()).Template);

    [Fact]
    public async Task CustomerDetail_UsesOwnershipScopedRepositoryBoundary()
    {
        var service = new Mock<IQuotationService>();
        service.Setup(value => value.GetCustomerQuotationAsync(42, 7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CustomerQuotationDetails(Quotation(7, 42), [], [], []));
        var controller = Controller(service, AuthorizationResult.Failed());

        var result = await controller.GetQuotationAsync(7, 42, CancellationToken.None);

        var details = Assert.IsType<CustomerQuotationDetails>(result.Value);
        Assert.Equal(42, details.Quotation.CustomerId);
        service.Verify(value => value.GetQuotationAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnscopedDetail_RequiresAdministrativeReadPermission()
    {
        var service = new Mock<IQuotationService>();
        var controller = Controller(service, AuthorizationResult.Failed());

        var result = await controller.GetQuotationAsync(7, null, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        service.VerifyNoOtherCalls();
    }

    private static QuotationsController Controller(
        Mock<IQuotationService> service,
        AuthorizationResult administrativeRead)
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization.Setup(value => value.AuthorizeAsync(
                It.IsAny<System.Security.Claims.ClaimsPrincipal>(),
                null,
                "Permission:legacy.quotations.read"))
            .ReturnsAsync(administrativeRead);
        var controller = new QuotationsController(
            service.Object,
            Mock.Of<IIdempotencyStore>(),
            authorization.Object,
            Mock.Of<IQuotationDecisionWorkflow>());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return controller;
    }

    private static QuotationResponse Quotation(int id, int customerId) => new(
        id,
        customerId,
        null,
        null,
        30,
        new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
        100m,
        7m,
        107m,
        3m,
        104m,
        764,
        null,
        null,
        null,
        null,
        null,
        null,
        null);

}
