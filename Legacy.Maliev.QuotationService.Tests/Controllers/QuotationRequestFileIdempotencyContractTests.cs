using System.Reflection;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        var controller = new QuotationRequestFilesController(service.Object, SerializedIdempotencyStore());

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

        Assert.IsType<ConflictObjectResult>(conflict);
        service.Verify(value => value.CreateRequestFileAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Once);
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

        public Task<T?> GetAsync<T>(string scope, string key, CancellationToken cancellationToken) where T : class =>
            Task.FromResult(values.TryGetValue($"{scope}:{key}", out var value) ? (T)value : null);

        public Task SetAsync<T>(string scope, string key, T response, CancellationToken cancellationToken) where T : class
        {
            values[$"{scope}:{key}"] = response;
            return Task.CompletedTask;
        }
    }

    private static IIdempotencyStore SerializedIdempotencyStore() => new DistributedQuotationCache(
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions())),
        NullLogger<DistributedQuotationCache>.Instance);
}
