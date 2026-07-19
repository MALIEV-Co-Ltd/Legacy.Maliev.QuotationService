using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

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
}
