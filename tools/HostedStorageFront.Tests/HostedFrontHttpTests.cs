using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Cloud.Storage.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StorageObject = Google.Apis.Storage.v1.Data.Object;

namespace InvoiceCompletionProducerAcceptance.Companion;

// Actual Kestrel sockets and installed SDK, with explicitly controlled observers and backend.
// These controls do not establish real File process ownership, backend identity or financial completion.
public sealed class HostedFrontHttpTests
{
    private const string UploadPath = "/upload/storage/v1/b/maliev.com/o";
    private const string SessionQuery = "?uploadType=resumable&name=quarantine%2Fpart.pdf&upload_id=controlled-session";

    [Fact]
    public async Task ActualSdkForwardsThreeChunksAndPreservesGenerationAndCompleteBytes()
    {
        byte[] expected = Enumerable.Range(0, (512 * 1024) + 17).Select(index => (byte)(index % 251)).ToArray();
        using var received = new MemoryStream();
        int initiations = 0, chunks = 0, incomplete = 0;
        await using var fixture = await Fixture.StartAsync(async context =>
        {
            Assert.Equal(UploadPath, context.Request.Path.Value);
            if (context.Request.Method == "POST")
            {
                initiations++;
                Assert.Equal("0", context.Request.Query["ifGenerationMatch"].ToString());
                context.Response.Headers.Location = $"http://{context.Request.Host}{UploadPath}{SessionQuery}";
                return;
            }
            Assert.Equal("PUT", context.Request.Method);
            Assert.Equal("controlled-session", context.Request.Query["upload_id"].ToString());
            var range = ContentRangeHeaderValue.Parse(context.Request.Headers["Content-Range"].ToString());
            Assert.Equal(received.Length, range.From);
            Assert.Equal((long)expected.Length, range.Length);
            using var requestDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await context.Request.Body.CopyToAsync(received, requestDeadline.Token);
            chunks++;
            if (received.Length < expected.Length)
            {
                incomplete++;
                context.Response.StatusCode = 308;
                context.Response.Headers["Range"] = $"bytes=0-{received.Length - 1}";
                return;
            }
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                bucket = "maliev.com",
                name = "quarantine/part.pdf",
                generation = "17",
                size = received.Length.ToString(CultureInfo.InvariantCulture),
                crc32c = Crc32c(received.ToArray()),
            }), requestDeadline.Token);
        });
        await fixture.BootstrapAsync();
        using var client = new StorageClientBuilder { BaseUri = fixture.Origin.AbsoluteUri, UnauthenticatedAccess = true }.Build();
        using var content = new MemoryStream(expected);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await client.UploadObjectAsync(new StorageObject
        {
            Bucket = "maliev.com",
            Name = "quarantine/part.pdf",
            ContentType = "application/pdf",
        }, content, new UploadObjectOptions { IfGenerationMatch = 0, ChunkSize = 256 * 1024 }, deadline.Token);
        Assert.Equal(17L, result.Generation);
        Assert.Equal((ulong)expected.Length, result.Size);
        Assert.Equal(expected, received.ToArray());
        Assert.Equal((1, 3, 2), (initiations, chunks, incomplete));
        Assert.Equal(4, fixture.SdkObservations);
        Assert.True(fixture.BackendObservations >= 17);
    }

    [Fact]
    public async Task EmptyStatusProbeForwardsContentRangeAndAllows308WithoutLocation()
    {
        int calls = 0;
        await using var fixture = await Fixture.StartAsync(context =>
        {
            calls++;
            Assert.Equal("PUT", context.Request.Method);
            Assert.Equal(0L, context.Request.ContentLength);
            Assert.Equal("bytes */524305", context.Request.Headers["Content-Range"].ToString());
            context.Response.StatusCode = 308;
            return Task.CompletedTask;
        });
        await fixture.BootstrapAsync();
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(fixture.Origin, UploadPath + SessionQuery))
        { Content = new ByteArrayContent([]) };
        request.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */524305");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, deadline.Token);
        Assert.Equal(308, (int)response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.False(response.Headers.Contains("Range"));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorizationOrDifferentHostFailsBeforeCallerOrBackendObservation(bool differentHost)
    {
        int calls = 0;
        await using var fixture = await Fixture.StartAsync(_ => { calls++; return Task.CompletedTask; });
        await fixture.BootstrapAsync();
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(fixture.Origin, "/storage/v1/b/maliev.com/o/part.pdf"));
        if (differentHost) request.Headers.Host = "localhost:" + fixture.Origin.Port;
        else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "controlled-value");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.SendAsync(request, deadline.Token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal(0, fixture.SdkObservations);
        Assert.Equal(1, fixture.BackendObservations); // The sole observation was public bootstrap admission.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlledCallerOrBackendObservationFailureDeniesForwarding(bool backendFailure)
    {
        int calls = 0;
        await using var fixture = await Fixture.StartAsync(_ => { calls++; return Task.CompletedTask; });
        await fixture.BootstrapAsync();
        fixture.FailBackendObservation = backendFailure;
        fixture.FailCallerObservation = !backendFailure;
        using var client = Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(new Uri(fixture.Origin, "/storage/v1/b/maliev.com/o/part.pdf"), deadline.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal(1, fixture.SdkObservations);
    }

    [Fact]
    public async Task PendingPublicBootstrapReturns503WithoutBackendOrCallerObservation()
    {
        int calls = 0;
        await using var fixture = await Fixture.StartAsync(_ => { calls++; return Task.CompletedTask; });
        using var client = Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(new Uri(fixture.Origin, "/storage/v1/b/maliev.com/o/part.pdf"), deadline.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, calls);
        Assert.Equal(0, fixture.BackendObservations);
        Assert.Equal(0, fixture.SdkObservations);
    }

    [Fact]
    public async Task ForeignUploadSessionLocationIsRejectedWithoutFollowingIt()
    {
        int calls = 0;
        await using var fixture = await Fixture.StartAsync(context =>
        {
            calls++;
            context.Response.Headers.Location = "http://127.0.0.1:1" + UploadPath + SessionQuery;
            return Task.CompletedTask;
        });
        await fixture.BootstrapAsync();
        using var client = Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var body = new ByteArrayContent([]);
        using var response = await client.PostAsync(new Uri(fixture.Origin, UploadPath + "?uploadType=resumable"), body, deadline.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisposalCancelsActiveObservationAndPreventsSubsequentRequests()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(_ => Task.CompletedTask);
        await fixture.BootstrapAsync();
        fixture.CallerObserver = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        using var client = Client();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task<HttpResponseMessage> pending = client.GetAsync(new Uri(fixture.Origin, "/storage/v1/b/maliev.com/o/part.pdf"), deadline.Token);
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            fixture.Front.Dispose();
            using var response = await pending;
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var second = await client.GetAsync(new Uri(fixture.Origin, "/storage/v1/b/maliev.com/o/part.pdf"), deadline.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
            Assert.Equal(1, fixture.SdkObservations);
        }
        finally
        {
            fixture.Front.Dispose();
            try { using var response = await pending; }
            catch (OperationCanceledException) { }
            catch (HttpRequestException) { }
        }
    }

    [Fact]
    public async Task DisposalCancelsPendingBootstrapObserverWithoutInstallingAuthority()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(_ => Task.CompletedTask);
        fixture.FileObserver = async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task pending = fixture.BootstrapAsync();
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            fixture.Front.Dispose();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
            using var client = Client();
            using var response = await client.GetAsync(new Uri(fixture.Origin, "/storage/v1/b/maliev.com/o/part.pdf"), deadline.Token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal(0, fixture.SdkObservations);
            Assert.Equal(0, fixture.BackendObservations);
        }
        finally
        {
            fixture.Front.Dispose();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    private static HttpClient Client() => new(new SocketsHttpHandler
    { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(5) })
    { Timeout = TimeSpan.FromSeconds(10) };

    private static string Crc32c(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0x82F63B78U);
        }
        Span<byte> encoded = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(encoded, ~crc);
        return Convert.ToBase64String(encoded);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Listener backend;
        private readonly Listener listener;
        private readonly string runId = "c821-" + Guid.NewGuid().ToString("D");
        private readonly DateTimeOffset expiry = DateTimeOffset.UtcNow.AddMinutes(1);
        private int backendObservations;
        private int sdkObservations;
        public HostedStorageFront Front { get; }
        public Uri Origin => listener.Origin;
        public int BackendObservations => Volatile.Read(ref backendObservations);
        public int SdkObservations => Volatile.Read(ref sdkObservations);
        public bool FailBackendObservation { get; set; }
        public bool FailCallerObservation { get; set; }
        public Func<CancellationToken, Task>? CallerObserver { get; set; }
        public Func<CancellationToken, Task>? FileObserver { get; set; }

        private Fixture(Listener backend, Listener listener)
        {
            this.backend = backend;
            this.listener = listener;
            Front = new HostedStorageFront(Origin, backend.Origin, runId, expiry,
                _ =>
                {
                    Interlocked.Increment(ref backendObservations);
                    if (FailBackendObservation) throw new InvalidDataException("Controlled backend observation rejected.");
                    return Task.CompletedTask;
                }, async (_, token) =>
                {
                    if (FileObserver is not null) await FileObserver(token);
                }, async (_, _, token) =>
                {
                    Interlocked.Increment(ref sdkObservations);
                    if (FailCallerObservation) throw new InvalidDataException("Controlled caller observation rejected.");
                    if (CallerObserver is not null) await CallerObserver(token);
                });
        }

        public static async Task<Fixture> StartAsync(RequestDelegate handler)
        {
            Listener backend = await Listener.StartAsync(handler);
            Listener? listener = null;
            Fixture? fixture = null;
            try
            {
                listener = await Listener.StartAsync(context => fixture!.Front.HandleAsync(context));
                fixture = new Fixture(backend, listener);
                return fixture;
            }
            catch
            {
                try { if (listener is not null) await listener.DisposeAsync(); }
                finally { await backend.DisposeAsync(); }
                throw;
            }
        }

        public async Task BootstrapAsync()
        {
            using var key = RSA.Create(2048);
            var file = new ObservedFileHost(123, DateTimeOffset.UtcNow.AddSeconds(-1),
                Path.GetFullPath("controlled-file.dll"), new string('a', 64), runId, expiry);
            var message = new PublicSigningBootstrap("GOOG4-RSA-SHA256", Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), file);
            using var pipe = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(message));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            string observedHash = await Front.InstallPublicBootstrapAsync(pipe, deadline.Token);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())), observedHash);
        }

        public async ValueTask DisposeAsync()
        {
            try { Front.Dispose(); }
            finally
            {
                try { await listener.DisposeAsync(); }
                finally { await backend.DisposeAsync(); }
            }
        }
    }

    private sealed class Listener(WebApplication application, Uri origin) : IAsyncDisposable
    {
        public Uri Origin { get; } = origin;

        public static async Task<Listener> StartAsync(RequestDelegate handler)
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Limits.MaxRequestBodySize = 32 * 1024 * 1024;
                options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(2);
                options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                options.Listen(IPAddress.Loopback, 0);
            });
            var app = builder.Build();
            app.Run(handler);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await app.StartAsync(deadline.Token);
                var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
                var origin = new Uri(Assert.Single(addresses.Addresses) + "/");
                Assert.Equal("127.0.0.1", origin.Host);
                Assert.InRange(origin.Port, 1, 65535);
                return new Listener(app, origin);
            }
            catch
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await app.StopAsync(deadline.Token); }
                finally { await app.DisposeAsync(); }
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await application.StopAsync(deadline.Token); }
            finally { await application.DisposeAsync(); }
        }
    }
}
