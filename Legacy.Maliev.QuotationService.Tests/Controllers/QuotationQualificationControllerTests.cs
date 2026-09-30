using System.Security.Claims;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationQualificationControllerTests
{
    [Fact]
    public async Task Update_NormalizesEmployeeTransitionAndReturnsReceipt()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        var receipt = new QualificationReceipt(7, Guid.NewGuid(), "request-7", "qualified", null, 1, []);
        service.Setup(value => value.UpdateRequestQualificationAsync(
                7,
                new QualificationStateUpdateRequest("qualified", null, "complete", 0, null, "retry-1", 0),
                "employee-42",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualificationUpdateResult(QualificationUpdateStatus.Completed, receipt));
        var controller = Controller(service.Object, "employee-42");

        var result = await controller.UpdateQualificationStateAsync(
            7,
            new QualificationStateUpdateRequest(" QUALIFIED ", null, " COMPLETE ", 0, null, " retry-1 ", 0),
            CancellationToken.None);

        Assert.Same(receipt, Assert.IsType<OkObjectResult>(result.Result).Value);
        service.VerifyAll();
    }

    [Theory]
    [InlineData("unknown", null)]
    [InlineData("duplicate", null)]
    [InlineData("", "reason")]
    public async Task Update_RejectsInvalidTransitionsBeforePersistence(string state, string? reason)
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        var controller = Controller(service.Object, "employee-42");

        var result = await controller.UpdateQualificationStateAsync(
            7,
            new QualificationStateUpdateRequest(state, reason, null, 0, null, "retry-1", 0),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Update_RequiresAuthenticatedActorIdentity()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        var controller = Controller(service.Object, null);

        var result = await controller.UpdateQualificationStateAsync(
            7,
            new QualificationStateUpdateRequest("qualified", null, null, 0, null, "retry-1", 0),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Update_DoesNotAcceptDisplayNameAsStableActorIdentity()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        var controller = new QuotationRequestsController(service.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "employee@example.test")],
                        "test")),
                },
            },
        };

        var result = await controller.UpdateQualificationStateAsync(
            7,
            new QualificationStateUpdateRequest("qualified", null, null, 0, null, "retry-1", 0),
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public void QualificationReceipt_DoesNotExposeInternalIdempotencyKey()
    {
        var receipt = new QualificationReceipt(
            7,
            Guid.NewGuid(),
            "request-7",
            "qualified",
            DateTime.UtcNow,
            1,
            [new QualificationReceiptEvent(1, "unreviewed", "qualified", 1, DateTime.UtcNow, "employee-42", null, 0, null, null)]);

        var json = JsonSerializer.Serialize(receipt);

        Assert.DoesNotContain("IdempotencyKey", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutcomeReadback_IsEmployeeOnlyAndUsesCamelCasePiiFreeWire()
    {
        var from = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        service.Setup(value => value.GetQualificationOutcomeReadbackAsync(from, to, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualificationOutcomeReadback(from, to,
            [new(7, from.AddHours(1), "request-7", Guid.Parse("5cda380d-fd95-4fe4-bd5c-b378f7515160"), "qualified"),
             new(8, from.AddHours(2), null, null, "unreviewed")]));
        var employee = Controller(service.Object, "employee-42", employeeRole: true);
        var result = Assert.IsType<JsonResult>(await employee.GetQualificationOutcomeReadbackAsync(
            from, to, CancellationToken.None));
        var options = Assert.IsType<JsonSerializerOptions>(result.SerializerSettings);
        string body = JsonSerializer.Serialize(result.Value, options);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(new[] { "fromUtc", "requests", "toUtc" },
            json.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var requests = json.RootElement.GetProperty("requests");
        Assert.Equal("request-7", requests[0].GetProperty("transactionId").GetString());
        Assert.Equal("qualified", requests[0].GetProperty("state").GetString());
        Assert.False(requests[1].TryGetProperty("transactionId", out _));
        Assert.False(requests[1].TryGetProperty("journeyId", out _));
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);

        var customer = Controller(service.Object, "customer-42");
        Assert.IsType<ForbidResult>(await customer.GetQualificationOutcomeReadbackAsync(
            from, to, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await employee.GetQualificationOutcomeReadbackAsync(
            DateTime.SpecifyKind(from, DateTimeKind.Unspecified), to, CancellationToken.None));
        Assert.IsType<BadRequestResult>(await employee.GetQualificationOutcomeReadbackAsync(
            from, from.AddDays(32), CancellationToken.None));
        service.Verify(value => value.GetQualificationOutcomeReadbackAsync(from, to, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static QuotationRequestsController Controller(IQuotationService service, string? actor, bool employeeRole = false)
    {
        Claim[] claims = actor is null ? [] : employeeRole
            ? [new Claim("sub", actor), new Claim("identity_kind", "employee"), new Claim(ClaimTypes.Role, "Employee")]
            : [new Claim("sub", actor), new Claim("identity_kind", "employee")];
        return new QuotationRequestsController(service)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")),
                },
            },
        };
    }
}
