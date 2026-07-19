using System.Reflection;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationRequestFileIdempotencyContractTests
{
    [Fact]
    public void CreateRequestFile_DeclaresOptionalIdempotencyHeaderAndStoreDependency()
    {
        var constructor = Assert.Single(typeof(QuotationRequestFilesController).GetConstructors());
        Assert.Contains(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IIdempotencyStore));

        var action = typeof(QuotationRequestFilesController).GetMethod(
            nameof(QuotationRequestFilesController.CreateQuotationRequestFileEntryAsync));
        var header = Assert.Single(
            action!.GetParameters(),
            parameter => parameter.GetCustomAttribute<FromHeaderAttribute>()?.Name == "Idempotency-Key");

        Assert.Equal(
            NullabilityState.Nullable,
            new NullabilityInfoContext().Create(header).ReadState);
    }

    [Fact]
    public async Task CreateRequestFile_SameKeyAndTuple_ReplaysOriginalCreatedResponse()
    {
        var service = new Mock<IQuotationService>();
        service.Setup(value => value.CreateRequestFileAsync(
                417,
                "legacy-requests",
                "instant-quotation/417/file.stl",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotationRequestFileResponse(
                23,
                417,
                "legacy-requests",
                "instant-quotation/417/file.stl",
                null,
                null));
        var controller = new QuotationRequestFilesController(service.Object, new MemoryIdempotencyStore());

        var first = await controller.CreateQuotationRequestFileEntryAsync(
            417,
            "legacy-requests",
            "instant-quotation/417/file.stl",
            "submission:file:1",
            CancellationToken.None);
        var replay = await controller.CreateQuotationRequestFileEntryAsync(
            417,
            " legacy-requests ",
            " instant-quotation/417/file.stl ",
            "submission:file:1",
            CancellationToken.None);

        var firstResponse = Assert.IsType<QuotationRequestFileResponse>(
            Assert.IsType<CreatedAtRouteResult>(first).Value);
        var replayResponse = Assert.IsType<QuotationRequestFileResponse>(
            Assert.IsType<CreatedAtRouteResult>(replay).Value);
        Assert.Equal(23, firstResponse.Id);
        Assert.Equal(firstResponse, replayResponse);
        service.Verify(value => value.CreateRequestFileAsync(
            417,
            "legacy-requests",
            "instant-quotation/417/file.stl",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateRequestFile_SameKeyAndDifferentTuple_ReturnsConflictWithoutSecondInsert()
    {
        var service = new Mock<IQuotationService>();
        service.Setup(value => value.CreateRequestFileAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int requestId, string bucket, string objectName, CancellationToken _) =>
                new QuotationRequestFileResponse(23, requestId, bucket, objectName, null, null));
        var controller = new QuotationRequestFilesController(service.Object, new MemoryIdempotencyStore());

        await controller.CreateQuotationRequestFileEntryAsync(
            417,
            "legacy-requests",
            "instant-quotation/417/file.stl",
            "submission:file:1",
            CancellationToken.None);
        var conflict = await controller.CreateQuotationRequestFileEntryAsync(
            417,
            "legacy-requests",
            "instant-quotation/417/other.stl",
            "submission:file:1",
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(conflict);
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("idempotency_key_conflict", problem.Extensions["code"]);
        Assert.Contains("application/problem+json", result.ContentTypes);
        service.Verify(value => value.CreateRequestFileAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateRequestFile_OverlongKey_ReturnsStableProblemWithoutInsert()
    {
        var service = new Mock<IQuotationService>();
        var controller = new QuotationRequestFilesController(service.Object, new MemoryIdempotencyStore());

        var response = await controller.CreateQuotationRequestFileEntryAsync(
            417,
            "legacy-requests",
            "instant-quotation/417/file.stl",
            new string('x', 129),
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("idempotency_key_too_long", problem.Extensions["code"]);
        Assert.Contains("application/problem+json", result.ContentTypes);
        service.Verify(value => value.CreateRequestFileAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateRequestFile_ConcurrentFirstUseWithDifferentTuple_CreatesOnlyBoundTuple()
    {
        var service = new Mock<IQuotationService>();
        service.Setup(value => value.CreateRequestFileAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int requestId, string bucket, string objectName, CancellationToken _) =>
                new QuotationRequestFileResponse(23, requestId, bucket, objectName, null, null));
        var controller = new QuotationRequestFilesController(service.Object, new MemoryIdempotencyStore());

        var first = controller.CreateQuotationRequestFileEntryAsync(
            417, "legacy-requests", "instant-quotation/417/first.stl", "submission:file:1", CancellationToken.None);
        var second = controller.CreateQuotationRequestFileEntryAsync(
            417, "legacy-requests", "instant-quotation/417/second.stl", "submission:file:1", CancellationToken.None);
        var responses = await Task.WhenAll(first, second);

        Assert.Single(responses, value => value is CreatedAtRouteResult);
        Assert.Single(responses, value => value is ObjectResult { StatusCode: StatusCodes.Status409Conflict });
        service.Verify(value => value.CreateRequestFileAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CreateRequestFile_IdempotencyStoreUnavailable_FailsClosedWithoutInsert()
    {
        var service = new Mock<IQuotationService>();
        var store = new Mock<IIdempotencyStore>();
        store.Setup(value => value.BindAsync(
                "quotation-request-file",
                "submission:file:1",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(IdempotencyBindingResult.Unavailable);
        var controller = new QuotationRequestFilesController(service.Object, store.Object);

        var response = await controller.CreateQuotationRequestFileEntryAsync(
            417,
            "legacy-requests",
            "instant-quotation/417/file.stl",
            "submission:file:1",
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal("idempotency_store_unavailable", problem.Extensions["code"]);
        Assert.Contains("application/problem+json", result.ContentTypes);
        service.Verify(value => value.CreateRequestFileAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateRequestFile_MissingKey_PreservesLegacyCallerCompatibility()
    {
        var service = new Mock<IQuotationService>();
        service.SetupSequence(value => value.CreateRequestFileAsync(
                417,
                "legacy-requests",
                "instant-quotation/417/file.stl",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotationRequestFileResponse(23, 417, "legacy-requests", "instant-quotation/417/file.stl", null, null))
            .ReturnsAsync(new QuotationRequestFileResponse(24, 417, "legacy-requests", "instant-quotation/417/file.stl", null, null));
        var controller = new QuotationRequestFilesController(service.Object, new MemoryIdempotencyStore());

        var first = await controller.CreateQuotationRequestFileEntryAsync(
            417,
            "legacy-requests",
            "instant-quotation/417/file.stl",
            null,
            CancellationToken.None);
        var second = await controller.CreateQuotationRequestFileEntryAsync(
            417,
            "legacy-requests",
            "instant-quotation/417/file.stl",
            null,
            CancellationToken.None);

        Assert.IsType<CreatedAtRouteResult>(first);
        Assert.IsType<CreatedAtRouteResult>(second);
        service.Verify(value => value.CreateRequestFileAsync(
            417,
            "legacy-requests",
            "instant-quotation/417/file.stl",
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    private sealed class MemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly Dictionary<string, object> values = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> bindings = new(StringComparer.Ordinal);
        private readonly Lock sync = new();

        public Task<IdempotencyBindingResult> BindAsync(
            string scope,
            string key,
            string fingerprint,
            CancellationToken cancellationToken)
        {
            lock (sync)
            {
                var storageKey = $"{scope}:{key}";
                if (!bindings.TryGetValue(storageKey, out var existing))
                {
                    bindings[storageKey] = fingerprint;
                    return Task.FromResult(IdempotencyBindingResult.Acquired);
                }

                return Task.FromResult(existing == fingerprint
                    ? IdempotencyBindingResult.Matched
                    : IdempotencyBindingResult.Conflict);
            }
        }

        public Task<T?> GetAsync<T>(string scope, string key, CancellationToken cancellationToken) where T : class =>
            Task.FromResult(values.TryGetValue($"{scope}:{key}", out var value) ? (T)value : null);

        public Task SetAsync<T>(string scope, string key, T response, CancellationToken cancellationToken) where T : class
        {
            values[$"{scope}:{key}"] = response;
            return Task.CompletedTask;
        }
    }
}
