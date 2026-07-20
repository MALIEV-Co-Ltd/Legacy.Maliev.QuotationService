using System.Reflection;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationRequestCreateIdempotencyContractTests
{
    [Fact]
    public void CreateRequest_PreservesRoutePermissionAndOptionalIdempotencyHeader()
    {
        var action = typeof(QuotationRequestsController).GetMethod(
            nameof(QuotationRequestsController.CreateQuotationRequestAsync))!;
        Assert.Null(Assert.Single(action.GetCustomAttributes<HttpPostAttribute>()).Template);
        var permission = Assert.Single(action.GetCustomAttributes<RequirePermissionAttribute>());
        Assert.Equal("legacy.quotation-requests.create", permission.Permission);
        Assert.True(permission.RequireLiveCheck);
        var header = Assert.Single(
            action.GetParameters(),
            parameter => parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name == "Idempotency-Key");
        Assert.Equal(NullabilityState.Nullable, new NullabilityInfoContext().Create(header).ReadState);
    }

    [Fact]
    public async Task CreateRequest_OverlongKey_ReturnsStableProblemWithoutInsert()
    {
        var service = new Mock<IQuotationService>();
        service.Setup(value => value.CreateRequestIdempotentlyAsync(
                It.IsAny<UpsertQuotationRequestRequest>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotentRequestCreateResult(Response(17, "Ada"), IdempotencyBindingResult.Acquired));
        var controller = new QuotationRequestsController(service.Object);

        var response = await controller.CreateQuotationRequestAsync(
            Request("Ada"),
            new string('x', 129),
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);
        Assert.Equal(
            "idempotency_key_too_long",
            Assert.IsType<ProblemDetails>(result.Value).Extensions["code"]);
        service.Verify(value => value.CreateRequestAsync(
            It.IsAny<UpsertQuotationRequestRequest>(),
            It.IsAny<CancellationToken>()), Times.Never);
        service.Verify(value => value.CreateRequestIdempotentlyAsync(
            It.IsAny<UpsertQuotationRequestRequest>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateRequest_WhitespaceKey_ReturnsStableProblemWithoutInsert()
    {
        var service = new Mock<IQuotationService>();
        service.Setup(value => value.CreateRequestAsync(
                It.IsAny<UpsertQuotationRequestRequest>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response(17, "Ada"));
        var controller = new QuotationRequestsController(service.Object);

        var response = await controller.CreateQuotationRequestAsync(
            Request("Ada"),
            "   ",
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);
        Assert.Equal(
            "idempotency_key_invalid",
            Assert.IsType<ProblemDetails>(result.Value).Extensions["code"]);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CreateRequest_SameKeyAndChangedPayload_ReturnsStableConflictWithoutSecondInsert()
    {
        var service = new Mock<IQuotationService>();
        string? boundFingerprint = null;
        service.Setup(value => value.CreateRequestIdempotentlyAsync(
                It.IsAny<UpsertQuotationRequestRequest>(),
                It.Is<string>(keyHash => keyHash.Length == 64),
                It.Is<string>(fingerprint => fingerprint.Length == 64),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((UpsertQuotationRequestRequest request, string _, string fingerprint, CancellationToken _) =>
            {
                if (boundFingerprint is null)
                {
                    boundFingerprint = fingerprint;
                    return new IdempotentRequestCreateResult(
                        Response(17, request.FirstName),
                        IdempotencyBindingResult.Acquired);
                }

                return fingerprint == boundFingerprint
                    ? new IdempotentRequestCreateResult(Response(17, "Ada"), IdempotencyBindingResult.Matched)
                    : new IdempotentRequestCreateResult(null, IdempotencyBindingResult.Conflict);
            });
        var controller = new QuotationRequestsController(service.Object);

        var first = await controller.CreateQuotationRequestAsync(
            Request("Ada"),
            "legacy-web-quotation-11111111222233334444555555555555",
            CancellationToken.None);
        var changed = await controller.CreateQuotationRequestAsync(
            Request("Grace"),
            "legacy-web-quotation-11111111222233334444555555555555",
            CancellationToken.None);

        Assert.IsType<CreatedAtRouteResult>(first);
        var result = Assert.IsType<ObjectResult>(changed);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);
        Assert.Equal(
            "idempotency_key_conflict",
            Assert.IsType<ProblemDetails>(result.Value).Extensions["code"]);
        service.Verify(value => value.CreateRequestIdempotentlyAsync(
            It.IsAny<UpsertQuotationRequestRequest>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task CreateRequest_SameKeyAndPayload_ReplaysExactCreatedRouteAndBody()
    {
        var response = Response(17, "Ada");
        var service = new Mock<IQuotationService>();
        service.SetupSequence(value => value.CreateRequestIdempotentlyAsync(
                It.IsAny<UpsertQuotationRequestRequest>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdempotentRequestCreateResult(response, IdempotencyBindingResult.Acquired))
            .ReturnsAsync(new IdempotentRequestCreateResult(response, IdempotencyBindingResult.Matched));
        var controller = new QuotationRequestsController(service.Object);

        var first = Assert.IsType<CreatedAtRouteResult>(await controller.CreateQuotationRequestAsync(
            Request("Ada"),
            "legacy-web-quotation-11111111222233334444555555555555",
            CancellationToken.None));
        var replay = Assert.IsType<CreatedAtRouteResult>(await controller.CreateQuotationRequestAsync(
            Request("Ada"),
            "legacy-web-quotation-11111111222233334444555555555555",
            CancellationToken.None));

        Assert.Equal("GetQuotationRequest", first.RouteName);
        Assert.Equal(17, first.RouteValues!["requestId"]);
        Assert.Equal(first.RouteName, replay.RouteName);
        Assert.Equal(first.RouteValues, replay.RouteValues);
        Assert.Equal(response, Assert.IsType<QuotationRequestResponse>(first.Value));
        Assert.Equal(first.Value, replay.Value);
    }

    private static UpsertQuotationRequestRequest Request(string firstName) => new(
        firstName,
        "Lovelace",
        "quote@example.test",
        null,
        "TH",
        null,
        null,
        "Please quote this model.",
        null,
        null);

    private static QuotationRequestResponse Response(int id, string? firstName) => new(
        id,
        firstName,
        "Lovelace",
        "quote@example.test",
        null,
        "TH",
        null,
        null,
        "Please quote this model.",
        null,
        null,
        new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 7, 20, 0, 0, 0, DateTimeKind.Utc));

}
