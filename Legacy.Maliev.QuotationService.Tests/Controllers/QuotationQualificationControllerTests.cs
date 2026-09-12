using System.Security.Claims;
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

    private static QuotationRequestsController Controller(IQuotationService service, string? actor)
    {
        Claim[] claims = actor is null ? [] : [new Claim(ClaimTypes.NameIdentifier, actor)];
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
