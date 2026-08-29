using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Caching;

public sealed class DistributedQuotationCacheTests
{
    [Fact]
    public async Task Idempotency_RoundTripsResponseAndHashesExternalKey()
    {
        IDistributedCache distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        IIdempotencyStore store = new DistributedQuotationCache(distributed, NullLogger<DistributedQuotationCache>.Instance);
        var response = new QuotationRequestResponse(9, "A", null, null, null, null, null, null, null, null, null, null, null);
        await store.SetAsync("quotation-request", "external-key", response, CancellationToken.None);
        Assert.Equal(response, await store.GetAsync<QuotationRequestResponse>("quotation-request", "external-key", CancellationToken.None));
        Assert.Null(await distributed.GetAsync("idempotency:quotation-request:external-key"));
    }

    [Fact]
    public async Task IdempotencyBinding_WithoutRedis_FailsClosed()
    {
        IDistributedCache distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        IIdempotencyStore store = new DistributedQuotationCache(distributed, NullLogger<DistributedQuotationCache>.Instance);

        var result = await store.BindAsync(
            "quotation-request-file",
            "external-key",
            "fingerprint",
            CancellationToken.None);

        Assert.Equal(IdempotencyBindingResult.Unavailable, result);
    }

    [Fact]
    public async Task GetAsync_does_not_swallow_cancellation()
    {
        var cancellationToken = new CancellationToken(canceled: true);
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellationToken));
        var cache = new DistributedQuotationCache(distributed.Object, NullLogger<DistributedQuotationCache>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cache.GetAsync<QuotationRequestResponse>("key", cancellationToken));
    }

    [Fact]
    public async Task SetAsync_does_not_swallow_cancellation()
    {
        var cancellationToken = new CancellationToken(canceled: true);
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.SetAsync(
                It.IsAny<string>(),
                It.IsAny<byte[]>(),
                It.IsAny<DistributedCacheEntryOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellationToken));
        var cache = new DistributedQuotationCache(distributed.Object, NullLogger<DistributedQuotationCache>.Instance);
        var response = new QuotationRequestResponse(9, "A", null, null, null, null, null, null, null, null, null, null, null);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => cache.SetAsync("key", response, TimeSpan.FromMinutes(1), cancellationToken));
    }

    [Fact]
    public async Task RemoveAsync_does_not_swallow_cancellation()
    {
        var cancellationToken = new CancellationToken(canceled: true);
        var distributed = new Mock<IDistributedCache>();
        distributed.Setup(value => value.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cancellationToken));
        var cache = new DistributedQuotationCache(distributed.Object, NullLogger<DistributedQuotationCache>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => cache.RemoveAsync("key", cancellationToken));
    }

    [Fact]
    public async Task BindAsync_does_not_swallow_pre_cancelled_request()
    {
        var cancellationToken = new CancellationToken(canceled: true);
        IDistributedCache distributed = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        IIdempotencyStore store = new DistributedQuotationCache(distributed, NullLogger<DistributedQuotationCache>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() => store.BindAsync(
            "quotation-request-file",
            "external-key",
            "fingerprint",
            cancellationToken));
    }
}
