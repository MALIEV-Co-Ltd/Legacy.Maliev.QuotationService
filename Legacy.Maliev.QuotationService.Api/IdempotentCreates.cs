using Legacy.Maliev.QuotationService.Application.Interfaces;

namespace Legacy.Maliev.QuotationService.Api;

internal static class IdempotentCreates
{
    internal sealed record BoundCreateResult<T>(T? Response, IdempotencyBindingResult Binding) where T : class
    {
        public bool Conflict => Binding == IdempotencyBindingResult.Conflict;
        public bool Unavailable => Binding == IdempotencyBindingResult.Unavailable;
    }

    private sealed record BoundCreateEnvelope<T>(string Fingerprint, T Response) where T : class;

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

    public static async Task<BoundCreateResult<T>> GetOrCreateBoundNullableAsync<T>(
        IIdempotencyStore store,
        string scope,
        string? key,
        string fingerprint,
        Func<Task<T?>> create,
        CancellationToken cancellationToken)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return new(await create(), IdempotencyBindingResult.Acquired);
        }

        var binding = await store.BindAsync(scope, key, fingerprint, cancellationToken);
        if (binding is IdempotencyBindingResult.Conflict or IdempotencyBindingResult.Unavailable)
        {
            return new(null, binding);
        }

        var existing = await store.GetAsync<BoundCreateEnvelope<T>>(scope, key, cancellationToken);
        if (existing is not null)
        {
            return string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal)
                ? new(existing.Response, binding)
                : new(null, IdempotencyBindingResult.Conflict);
        }

        var response = await create();
        if (response is not null)
        {
            await store.SetAsync(
                scope,
                key,
                new BoundCreateEnvelope<T>(fingerprint, response),
                cancellationToken);
        }

        return new(response, binding);
    }
}
