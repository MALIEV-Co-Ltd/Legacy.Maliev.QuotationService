using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Legacy.Maliev.QuotationService.Data;

/// <summary>Redis adapter for authorized read caching and create-response idempotency.</summary>
public sealed class DistributedQuotationCache(
    IDistributedCache cache,
    ILogger<DistributedQuotationCache> logger,
    IConnectionMultiplexer? redis = null) : IQuotationCache, IIdempotencyStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan IdempotencyLifetime = TimeSpan.FromHours(24);

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class
    {
        try
        {
            var bytes = await cache.GetAsync(key, cancellationToken);
            return bytes is null ? default : JsonSerializer.Deserialize<T>(bytes, JsonOptions);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Quotation cache read failed; using PostgreSQL");
            return default;
        }
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken) where T : class
    {
        try
        {
            await cache.SetAsync(key, JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions), new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = lifetime,
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Quotation cache write failed; continuing without cache");
        }
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await cache.RemoveAsync(key, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Quotation cache invalidation failed");
        }
    }

    /// <inheritdoc />
    public async Task<IdempotencyBindingResult> BindAsync(
        string scope,
        string key,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var storageKey = IdempotencyBindingKey(scope, key);
        if (redis is null)
        {
            logger.LogWarning("Redis is unavailable; rejecting replay-sensitive create");
            return IdempotencyBindingResult.Unavailable;
        }

        try
        {
            var database = redis.GetDatabase();
            if (await database.StringSetAsync(storageKey, fingerprint, IdempotencyLifetime, When.NotExists))
            {
                return IdempotencyBindingResult.Acquired;
            }

            var existing = await database.StringGetAsync(storageKey);
            if (!existing.HasValue)
            {
                logger.LogWarning("Idempotency binding expired during comparison; failing closed");
                return IdempotencyBindingResult.Unavailable;
            }

            return string.Equals(existing.ToString(), fingerprint, StringComparison.Ordinal)
                ? IdempotencyBindingResult.Matched
                : IdempotencyBindingResult.Conflict;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Idempotency binding failed; rejecting replay-sensitive create");
            return IdempotencyBindingResult.Unavailable;
        }
    }

    /// <inheritdoc />
    async Task<T?> IIdempotencyStore.GetAsync<T>(string scope, string key, CancellationToken cancellationToken) where T : class =>
        await GetAsync<T>(IdempotencyKey(scope, key), cancellationToken);

    /// <inheritdoc />
    Task IIdempotencyStore.SetAsync<T>(string scope, string key, T response, CancellationToken cancellationToken) where T : class =>
        SetAsync(IdempotencyKey(scope, key), response, TimeSpan.FromHours(24), cancellationToken);

    private static string IdempotencyKey(string scope, string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return $"idempotency:{scope}:{hash}";
    }

    private static string IdempotencyBindingKey(string scope, string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return $"legacy:quotation:idempotency-binding:{scope}:{hash}";
    }
}
