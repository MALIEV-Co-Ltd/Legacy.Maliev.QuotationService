using System.Reflection;
using System.Security.Claims;
using Legacy.Maliev.QuotationService.Api.Authorization;
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
    public static TheoryData<Claim[], bool> EmployeeDecisionCallers => new()
    {
        { [new Claim("sub", "joined-employee"), new Claim("identity_kind", "employee")], true },
        { [new Claim("identity_kind", "service"), new Claim("sub", "service:legacy-intranet"), new Claim("permissions", "legacy.quotations.update")], true },
        { [new Claim(ClaimTypes.Role, "Customer"), new Claim("identity_kind", "customer"), new Claim("permissions", "legacy.quotations.update")], false },
        { [new Claim("identity_kind", "service"), new Claim("sub", "service:other"), new Claim("permissions", "legacy.quotations.update")], false },
        { [new Claim("identity_kind", "service"), new Claim("sub", "service:legacy-intranet")], false },
        { [new Claim("identity_kind", "service"), new Claim("sub", "service:legacy-intranet"), new Claim("permissions", "*")], false },
        { [new Claim("identity_kind", "service"), new Claim("sub", "service:legacy-intranet"), new Claim("permissions", "legacy.quotations.update"), new Claim("permissions", "legacy.quotations.*")], false },
        { [new Claim("identity_kind", "service"), new Claim("identity_kind", "employee"), new Claim("sub", "service:legacy-intranet"), new Claim("permissions", "legacy.quotations.update")], false },
        { [new Claim("identity_kind", "service"), new Claim("sub", "service:legacy-intranet"), new Claim("permission", "legacy.quotations.update")], false },
        { [new Claim(ClaimTypes.Role, "Employee"), new Claim("identity_kind", "customer")], false },
    };

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
    public void Controllers_PreserveLegacyRoutesAndAddOutcomeAndQualificationRoutes()
    {
        var methods = Controllers.SelectMany(row => ((Type)row[0]).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)).ToArray();
        Assert.Equal(39, methods.Length);
        Assert.Equal(40, methods.SelectMany(method => method.GetCustomAttributes<HttpMethodAttribute>()).Count());
        var qualification = methods.Where(method => method.GetCustomAttribute<QualificationAuthorityAttribute>() is not null).ToArray();
        Assert.Equal(2, qualification.Length);
        Assert.All(qualification, method =>
        {
            Assert.Equal(typeof(QuotationRequestsController), method.DeclaringType);
            Assert.Empty(method.GetCustomAttributes<RequirePermissionAttribute>());
            Assert.Single(method.GetCustomAttributes<QualificationAuthorityAttribute>());
            Assert.Contains(method.Name, new[] { nameof(QuotationRequestsController.GetQualificationReceiptAsync), nameof(QuotationRequestsController.UpdateQualificationStateAsync) });
        });
        var ordinary = methods.Except(qualification).ToArray();
        Assert.Equal(37, ordinary.Length);
        Assert.All(ordinary, method =>
        {
            Assert.Single(method.GetCustomAttributes<RequirePermissionAttribute>());
            Assert.Empty(method.GetCustomAttributes<QualificationAuthorityAttribute>());
        });
    }

    [Fact]
    public void QualificationBoundaries_UseLiveLeastPrivilegeRequestPermissions()
    {
        var update = typeof(QuotationRequestsController).GetMethod(nameof(QuotationRequestsController.UpdateQualificationStateAsync))!;
        Assert.Equal("{requestId:int}/qualification", Assert.Single(update.GetCustomAttributes<HttpPutAttribute>()).Template);
        var updatePermission = Assert.Single(update.GetCustomAttributes<QualificationAuthorityAttribute>());
        Assert.Equal(QuotationPermissions.RequestsUpdate, updatePermission.Permission);
        Assert.Empty(update.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(typeof(int), update.GetParameters().Single(parameter => parameter.Name == "requestId").ParameterType);
        Assert.Equal("quotation-request-qualification", QualificationAuthorityAttribute.Purpose);

        var receipt = typeof(QuotationRequestsController).GetMethod(nameof(QuotationRequestsController.GetQualificationReceiptAsync))!;
        Assert.Equal("{requestId:int}/qualification-receipt", Assert.Single(receipt.GetCustomAttributes<HttpGetAttribute>()).Template);
        var readPermission = Assert.Single(receipt.GetCustomAttributes<QualificationAuthorityAttribute>());
        Assert.Equal(QuotationPermissions.RequestsRead, readPermission.Permission);
        Assert.Empty(receipt.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(typeof(int), receipt.GetParameters().Single(parameter => parameter.Name == "requestId").ParameterType);

        var readback = typeof(QuotationRequestsController).GetMethod(nameof(QuotationRequestsController.GetQualificationOutcomeReadbackAsync))!;
        Assert.Equal("qualification-outcomes/readback", Assert.Single(readback.GetCustomAttributes<HttpGetAttribute>()).Template);
        Assert.Equal(QuotationEmployeeActorPolicy.Name, Assert.Single(readback.GetCustomAttributes<AuthorizeAttribute>(),
            attribute => attribute.GetType() == typeof(AuthorizeAttribute)).Policy);
        var readbackPermission = Assert.Single(readback.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(QuotationPermissions.RequestsRead, readbackPermission.Permission);
        Assert.True(readbackPermission.RequireLiveCheck);
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
    public void InvoiceOperationReadback_IsCriticalWriteAuthorityWithExactRecoveryDto()
    {
        var action = typeof(QuotationsController).GetMethod(nameof(QuotationsController.GetInvoiceCompletionOperationAsync))!;
        Assert.Equal("{quotationId:int}/invoice-completion/operations/{operationId:guid}",
            Assert.Single(action.GetCustomAttributes<HttpGetAttribute>()).Template);
        var permission = Assert.Single(action.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal("legacy.quotations.update", permission.Permission);
        Assert.True(permission.RequireLiveCheck);
        Assert.True(permission.IsCritical);
        Assert.Equal("/quotations/{quotationId}", permission.ResourcePathTemplate);
        Assert.Equal(new[] { "ContractVersion", "OperationId", "QuotationId", "InvoiceId", "OriginIssuer",
                "EmployeeSubject", "RequesterSubject", "ExecutorSubject", "OriginalQuotationVersion", "FinancialBinding",
                "FinancialBindingVersion", "State", "DecisionOrderVersion", "CompletedOrders", "TotalOrders", "ModifiedDate" },
            typeof(QuotationInvoiceCompletionReceipt).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task EmployeeInitiatedDecision_RequiresEmployeeRoleBeforeWorkflow()
    {
        var decisions = new Mock<IQuotationDecisionWorkflow>(MockBehavior.Strict);
        var controller = new QuotationsController(
            Mock.Of<IQuotationService>(),
            Mock.Of<IIdempotencyStore>(),
            Mock.Of<IAuthorizationService>(),
            decisions.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "customer-42"),
                        new Claim(ClaimTypes.Role, "Customer"),
                    ], "test")),
                },
            },
        };

        var result = await controller.DecideQuotationAsync(
            7,
            new QuotationDecisionRequest(Accepted: true, EmployeeInitiated: true),
            expected: null,
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        decisions.VerifyNoOtherCalls();
    }

    [Theory, MemberData(nameof(EmployeeDecisionCallers))]
    public async Task EmployeeInitiatedDecision_AcceptsOnlyEmployeeOrExactLegacyIntranetService(
        Claim[] claims,
        bool allowed)
    {
        var decisions = new Mock<IQuotationDecisionWorkflow>(MockBehavior.Strict);
        if (allowed)
        {
            decisions.Setup(value => value.DecideAsync(
                    7,
                    new QuotationDecisionRequest(Accepted: true, EmployeeInitiated: true),
                    null,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new QuotationDecisionResponse(
                    QuotationDecisionStatus.Completed,
                    CompletedOrders: 0,
                    TotalOrders: 0,
                    ModifiedDate: null));
        }

        var controller = new QuotationsController(
            Mock.Of<IQuotationService>(),
            Mock.Of<IIdempotencyStore>(),
            Mock.Of<IAuthorizationService>(),
            decisions.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                },
            },
        };

        var result = await controller.DecideQuotationAsync(
            7,
            new QuotationDecisionRequest(Accepted: true, EmployeeInitiated: true),
            expected: null,
            CancellationToken.None);

        if (allowed)
        {
            Assert.IsType<OkObjectResult>(result);
            decisions.VerifyAll();
        }
        else
        {
            Assert.IsType<ForbidResult>(result);
            decisions.VerifyNoOtherCalls();
        }
    }

    [Fact]
    public void OutcomeReadbackBoundary_IsEmployeeOnlyAndUsesLiveAdministrativeRead()
    {
        var action = typeof(QuotationsController).GetMethod(nameof(QuotationsController.GetOutcomeReadbackAsync))!;
        Assert.Equal("outcomes/readback", Assert.Single(action.GetCustomAttributes<HttpGetAttribute>()).Template);
        var actorPolicy = Assert.Single(action.GetCustomAttributes<AuthorizeAttribute>(), value => value.GetType() == typeof(AuthorizeAttribute));
        Assert.Equal(QuotationEmployeeActorPolicy.Name, actorPolicy.Policy);
        Assert.Null(actorPolicy.Roles);
        var permission = Assert.Single(action.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal(QuotationPermissions.QuotationsRead, permission.Permission);
        Assert.True(permission.RequireLiveCheck);
    }

    [Fact]
    public async Task OutcomeReadback_RejectsNonEmployeeAndInvalidUtcWindowsBeforeRepository()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var customerController = Controller(service, AuthorizationResult.Failed());
        customerController.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, "Customer"),
        ], "test"));
        var employeeController = Controller(service, AuthorizationResult.Failed());
        employeeController.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "employee-controller-fixture"),
            new Claim("identity_kind", "employee"),
        ], "test"));

        var forbidden = await customerController.GetOutcomeReadbackAsync(
            fromUtc,
            fromUtc.AddDays(1),
            CancellationToken.None);
        var empty = await employeeController.GetOutcomeReadbackAsync(
            fromUtc,
            fromUtc,
            CancellationToken.None);
        var nonUtc = await employeeController.GetOutcomeReadbackAsync(
            DateTime.SpecifyKind(fromUtc, DateTimeKind.Unspecified),
            fromUtc.AddDays(1),
            CancellationToken.None);
        var unbounded = await employeeController.GetOutcomeReadbackAsync(
            fromUtc,
            fromUtc.AddDays(31).AddTicks(1),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(forbidden.Result);
        Assert.IsType<BadRequestResult>(empty.Result);
        Assert.IsType<BadRequestResult>(nonUtc.Result);
        Assert.IsType<BadRequestResult>(unbounded.Result);
        service.VerifyNoOtherCalls();
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

    [Theory]
    [InlineData("permissions", "legacy.quotations.read", true)]
    [InlineData("permission", "legacy.quotations.read", true)]
    [InlineData("permissions", "*", false)]
    [InlineData("permissions", "legacy.quotation-requests.read", false)]
    public async Task UnscopedDetail_AcceptsOnlyExactSignedAdministrativeReadClaim(
        string claimType,
        string claimValue,
        bool expected)
    {
        var service = new Mock<IQuotationService>();
        service.Setup(value => value.GetQuotationAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Quotation(7, 42));
        var controller = Controller(service, AuthorizationResult.Failed());
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "service:legacy-intranet"),
            new Claim(claimType, claimValue),
        ], "test"));

        var result = await controller.GetQuotationAsync(7, null, CancellationToken.None);

        if (expected)
        {
            Assert.Equal(7, Assert.IsType<QuotationResponse>(result.Value).Id);
        }
        else
        {
            Assert.IsType<ForbidResult>(result.Result);
        }
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
