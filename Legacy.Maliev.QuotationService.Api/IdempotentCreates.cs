using Legacy.Maliev.QuotationService.Application.Interfaces;

namespace Legacy.Maliev.QuotationService.Api;

internal static class IdempotentCreates
{
    public static async Task<T> GetOrCreateAsync<T>(
        IIdempotencyStore store,
        string scope,
        string? key,
        Func<Task<T>> create,
        CancellationToken cancellationToken)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return await create();
        }

        var existing = await store.GetAsync<T>(scope, key, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var response = await create();
        await store.SetAsync(scope, key, response, cancellationToken);
        return response;
    }

    public static async Task<T?> GetOrCreateNullableAsync<T>(
        IIdempotencyStore store,
        string scope,
        string? key,
        Func<Task<T?>> create,
        CancellationToken cancellationToken)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return await create();
        }

        var existing = await store.GetAsync<T>(scope, key, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var response = await create();
        if (response is not null)
        {
            await store.SetAsync(scope, key, response, cancellationToken);
        }

        return response;
    }
}
