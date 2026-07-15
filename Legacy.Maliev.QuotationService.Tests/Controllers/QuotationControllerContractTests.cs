using System.Reflection;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

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
    public void Controllers_PreserveThirtyThreeActionsAndThirtyFourRouteTemplates()
    {
        var methods = Controllers.SelectMany(row => ((Type)row[0]).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)).ToArray();
        Assert.Equal(33, methods.Length);
        Assert.Equal(34, methods.SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()).Count());
        Assert.All(methods, method => Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>()));
    }

    [Fact]
    public void CustomerFilteredList_PreservesBothLegacyTemplates()
    {
        var routes = typeof(QuotationsController).GetMethod(nameof(QuotationsController.GetPaginatedQuotationAsync))!.GetCustomAttributes<HttpGetAttribute>().Select(x => x.Template).ToArray();
        Assert.Equal(new string?[] { null, "customers/{customerId:int}" }, routes);
    }

    [Theory]
    [InlineData(typeof(OrderItemsController), nameof(OrderItemsController.GetOrderItemsAsync), "/quotations/{quotationId:int}/orderitems")]
    [InlineData(typeof(OrdersController), nameof(OrdersController.CreateQuotationOrderLinkAsync), "/quotations/{quotationId:int}/orders/{orderId:int}")]
    [InlineData(typeof(OrdersController), nameof(OrdersController.GetAllOrdersFromQuotationAsync), "/quotations/{quotationId:int}/orders")]
    [InlineData(typeof(QuotationFilesController), nameof(QuotationFilesController.GetQuotationFilesAsync), "/quotations/{quotationId:int}/files")]
    [InlineData(typeof(QuotationRequestFilesController), nameof(QuotationRequestFilesController.GetQuotationRequestFilesAsync), "/quotationrequests/{requestId:int}/files")]
    public void CrossResourceRoutes_PreserveLegacyTemplates(Type controller, string action, string expected) =>
        Assert.Equal(expected, Assert.Single(controller.GetMethod(action)!.GetCustomAttributes<HttpMethodAttribute>()).Template);
}
