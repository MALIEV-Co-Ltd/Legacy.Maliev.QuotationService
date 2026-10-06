using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace InvoiceCompletionProducerAcceptance.Companion;

/// <summary>Prepared next source obligation: a real HTTP forwarding boundary, not an in-memory storage adapter.</summary>
public sealed class HostedStorageFront : IDisposable
{
    private const long MaximumBytes = 32 * 1024 * 1024;
    private readonly Uri origin;
    private readonly Uri backend;
    private readonly string runId;
    private readonly DateTimeOffset expiresUtc;
    private readonly HttpClient client;
    private readonly Func<CancellationToken, Task> observeBackend;
    private readonly Func<ObservedFileHost, CancellationToken, Task> observeFile;
    private readonly Func<ObservedFileHost, HttpContext, CancellationToken, Task> observeSdkCaller;
    private readonly object ownership = new();
    private readonly CancellationTokenSource ownerLifetime = new();
    private Binding? binding;
    private int bootstrapStarted;
    private int disposed;
    private int active;
    private bool released;
    private bool cancelling;

    // Backend identity/network/process observation and Kestrel listener ownership belong to the
    // outer owner. No instance may be activated until that concrete caller is reviewed and admitted.
    public HostedStorageFront(Uri observedFrontOrigin, ObservedStorageBackend observedOwnedBackend,
        string fixtureRunId, DateTimeOffset leaseExpiresUtc)
        : this(observedFrontOrigin, observedOwnedBackend.Origin, fixtureRunId, leaseExpiresUtc,
            observedOwnedBackend.ObserveAsync,
            (file, token) => CompanionLauncherAdmission.VerifyProcessAsync("File", file.Pid, file.StartedUtc,
                file.ExecutableDll, file.ExecutableSha256.ToUpperInvariant(), file.RunId, file.ExpiresUtc, token),
            (file, context, token) => new FileSdkCallerGuard(file).VerifyAsync(context, token))
    {
        observedOwnedBackend.Validate(DateTimeOffset.UtcNow);
    }

    // Only the native test assembly can supply controlled protocol/process observers. No runtime configuration selects them.
    internal HostedStorageFront(Uri observedFrontOrigin, Uri observedOwnedBridgeBackend,
        string fixtureRunId, DateTimeOffset leaseExpiresUtc, Func<CancellationToken, Task> backendObserver,
        Func<ObservedFileHost, CancellationToken, Task> fileObserver,
        Func<ObservedFileHost, HttpContext, CancellationToken, Task> sdkObserver)
    {
        if (observedFrontOrigin.Scheme != "http" || !IPAddress.TryParse(observedFrontOrigin.Host.Trim('[', ']'), out var host)
            || !IPAddress.IsLoopback(host) || observedFrontOrigin.AbsolutePath != "/" || observedFrontOrigin.Query != ""
            || observedFrontOrigin.Fragment != "" || observedFrontOrigin.UserInfo != ""
            || observedOwnedBridgeBackend.Scheme != "http" || observedOwnedBridgeBackend.AbsolutePath != "/"
            || observedOwnedBridgeBackend.Query != "" || observedOwnedBridgeBackend.Fragment != ""
            || observedOwnedBridgeBackend.UserInfo != ""
            || !IPAddress.TryParse(observedOwnedBridgeBackend.Host, out var privateIp)
            || privateIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
            || leaseExpiresUtc.Offset != TimeSpan.Zero || leaseExpiresUtc <= DateTimeOffset.UtcNow
            || leaseExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(30))
            throw new InvalidDataException("Exact observed front/backend origins and finite lease required.");
        origin = observedFrontOrigin;
        backend = observedOwnedBridgeBackend;
        runId = fixtureRunId;
        expiresUtc = leaseExpiresUtc;
        observeBackend = backendObserver;
        observeFile = fileObserver;
        observeSdkCaller = sdkObserver;
        client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            MaxResponseHeadersLength = 32,
            MaxConnectionsPerServer = 8,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<string> InstallPublicBootstrapAsync(Stream ownerPipe, CancellationToken cancellationToken)
    {
        lock (ownership)
        {
            if (bootstrapStarted != 0 || disposed != 0)
                throw new InvalidDataException("Public bootstrap is one-time and owner-bound.");
            bootstrapStarted = 1;
            active++;
        }
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ownerLifetime.Token);
            var message = await PublicSigningBootstrapReader.ReadAsync(ownerPipe, runId, expiresUtc, deadline.Token);
            await observeFile(message.File, deadline.Token);
            await observeBackend(deadline.Token);
            var admitted = new Binding(new HostedV4SignedReadVerifier(message.PublicKey, origin.Authority, expiresUtc),
                message.File);
            lock (ownership)
            {
                if (disposed != 0 || binding is not null || DateTimeOffset.UtcNow >= expiresUtc)
                {
                    admitted.Dispose();
                    throw new InvalidDataException("Public bootstrap cannot install into a closed or already bound owner.");
                }
                binding = admitted;
            }
            return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Convert.FromBase64String(message.PublicKey)));
        }
        finally
        {
            lock (ownership)
            {
                active--;
                if (disposed != 0 && active == 0 && !cancelling) Release();
            }
        }
    }

    public async Task HandleAsync(HttpContext context)
    {
        Binding? admitted;
        lock (ownership)
        {
            admitted = binding;
            if (disposed != 0 || DateTimeOffset.UtcNow >= expiresUtc || admitted is null || active >= 8)
            { context.Response.StatusCode = 503; return; }
            active++;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, ownerLifetime.Token);
        var remaining = expiresUtc - DateTimeOffset.UtcNow;
        try
        {
            if (remaining <= TimeSpan.Zero) { context.Response.StatusCode = 503; return; }
            deadline.CancelAfter(remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10));
            string rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "";
            if (rawTarget.Length is < 1 or > 16384 || !rawTarget.StartsWith('/') || rawTarget.Contains('#')
                || context.Request.Headers.Host.Count != 1 || context.Request.Headers.Host[0] != origin.Authority
                || context.Request.Headers.ContainsKey("Authorization"))
            { context.Response.StatusCode = 403; return; }
            bool sdk = IsSdkRoute(rawTarget, context.Request.Method);
            if (sdk)
            {
                await observeSdkCaller(admitted.File, context, deadline.Token);
            }
            else
            {
                if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")
                    || context.Request.Headers.Keys.Any(name => name.StartsWith("x-goog-", StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
                    || !Authorize(admitted, context.Request.Method, rawTarget))
                { context.Response.StatusCode = 403; return; }
            }
            if (context.Request.ContentLength is > MaximumBytes)
            { context.Response.StatusCode = 413; return; }
            await observeBackend(deadline.Token);
            var destination = new Uri(backend.GetLeftPart(UriPartial.Authority) + rawTarget, UriKind.Absolute);
            if (destination.GetLeftPart(UriPartial.Authority) != backend.GetLeftPart(UriPartial.Authority))
                throw new InvalidDataException("Request escaped actual owned backend origin.");
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), destination);
            if (sdk && (context.Request.Method is "POST" or "PUT" or "PATCH"
                || context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
            {
                request.Content = new StreamContent(new BoundedLeaseReadStream(context.Request.Body, MaximumBytes, expiresUtc));
                if (context.Request.ContentLength is long length) request.Content.Headers.ContentLength = length;
                if (context.Request.ContentType is string contentType)
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            }
            // SDK caller ownership is already proven; forward only transport headers needed by actual SDK operations.
            foreach (string name in new[] { "Content-Range", "x-goog-upload-command", "x-goog-upload-offset", "x-goog-upload-protocol", "x-goog-upload-header-content-length", "x-goog-upload-header-content-type" })
                if (sdk && context.Request.Headers.TryGetValue(name, out var value))
                {
                    if (name == "Content-Range" && request.Content is not null) request.Content.Headers.TryAddWithoutValidation(name, value.ToArray());
                    else request.Headers.TryAddWithoutValidation(name, value.ToArray());
                }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            RequireCurrent(deadline.Token);
            await observeBackend(deadline.Token);
            if (response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength is > MaximumBytes)
                throw new InvalidDataException("Unexpected encoded/oversized actual backend response.");
            var resumable = ResumableResponsePolicy.Read(response, sdk, context.Request.Method, rawTarget, backend, origin, MaximumBytes);
            // Re-admit ownership after response-policy validation, before exposing downstream headers/body.
            await observeBackend(deadline.Token);
            if (resumable.Location is string session) context.Response.Headers.Location = session;
            if (resumable.Range is string acknowledged) context.Response.Headers["Range"] = acknowledged;
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            if (response.Content.Headers.ContentLength is long responseLength) context.Response.ContentLength = responseLength;
            foreach (string name in new[] { "ETag", "x-goog-generation", "x-goog-hash" })
                if (response.Headers.TryGetValues(name, out var values)) context.Response.Headers[name] = string.Join(",", values);
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            await CopyResponseAsync(body, context.Response.Body, deadline.Token);
            await observeBackend(deadline.Token);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or HttpRequestException or OperationCanceledException
            or FormatException or ArgumentException)
        {
            if (context.Response.HasStarted) context.Abort();
            else { context.Response.Clear(); context.Response.StatusCode = 503; }
            // No signed URL, query, JWT, bootstrap key or backend response body is logged.
        }
        finally
        {
            lock (ownership)
            {
                active--;
                if (disposed != 0 && active == 0 && !cancelling) Release();
            }
        }
    }

    private bool Authorize(Binding admitted, string method, string rawTarget)
    {
        lock (admitted.SignatureLock)
            return admitted.SignedReads.Authorize(method, origin.Authority, rawTarget, DateTimeOffset.UtcNow);
    }

    private static bool IsSdkRoute(string rawTarget, string method)
    {
        if (method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE")) return false;
        string path = rawTarget.Split('?', 2)[0];
        string decoded = Uri.UnescapeDataString(path);
        if (decoded.Contains('%') || decoded.Contains('\\') || decoded.Contains("//")
            || decoded.Any(char.IsControl) || decoded.Split('/').Any(part => part is "." or "..")) return false;
        return decoded == "/storage/v1/b/maliev.com/o" || decoded.StartsWith("/storage/v1/b/maliev.com/o/", StringComparison.Ordinal)
            || decoded == "/upload/storage/v1/b/maliev.com/o";
    }

    private async Task CopyResponseAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[65536];
        long length = 0;
        while (true)
        {
            RequireCurrent(cancellationToken);
            int count = await source.ReadAsync(buffer, cancellationToken);
            RequireCurrent(cancellationToken);
            if (count == 0) break;
            length += count;
            if (length > MaximumBytes) throw new InvalidDataException("Actual backend body exceeded bounded copy.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            RequireCurrent(cancellationToken);
        }
    }

    private void RequireCurrent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (DateTimeOffset.UtcNow >= expiresUtc || Volatile.Read(ref disposed) != 0)
            throw new InvalidDataException("Owned front lease expired during HTTP operation.");
    }

    public void Dispose()
    {
        lock (ownership)
        {
            if (disposed != 0) return;
            disposed = 1;
            cancelling = true;
        }
        try { ownerLifetime.Cancel(); }
        finally
        {
            lock (ownership)
            {
                cancelling = false;
                if (active == 0) Release();
            }
        }
    }

    private void Release()
    {
        if (released) return;
        released = true;
        binding?.Dispose(); binding = null;
        client.Dispose(); ownerLifetime.Dispose();
    }

    private sealed record Binding(HostedV4SignedReadVerifier SignedReads, ObservedFileHost File) : IDisposable
    {
        internal object SignatureLock { get; } = new();
        public void Dispose() => SignedReads.Dispose();
    }
}
