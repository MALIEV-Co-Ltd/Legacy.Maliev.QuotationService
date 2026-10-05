using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Time.Testing;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Workload issuance/cache/deadline through actual Program and registered outbound consumers.</summary>
public sealed class QuotationWorkloadTokenLifecycleHttpTests(QuotationNormalIamFixture fixture)
    : IClassFixture<QuotationNormalIamFixture>
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Shared_owner_deadline_releases_all_consumers_and_cannot_cache_late_login(bool stallHeaders)
    {
        var login = new Login(stallHeaders);
        var downstream = new Downstream();
        await using var app = App(login, downstream, new FakeTimeProvider());
        using var bootstrap = app.CreateClient();
        var order = app.Services.GetRequiredService<IOrderDecisionClient>();
        using var authority = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("QualificationAuthority");
        using var iam = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");
        var orders = Enumerable.Range(0, 4).Select(index => order.TransitionAsync(115 + index, true, "workload-deadline", CancellationToken.None)).ToArray();
        var qualification = CheckAuthority(authority);
        var permission = iam.PostAsync("/iam/v1/auth/check-permission", null);
        try
        {
            await login.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(await Task.WhenAll(orders).WaitAsync(TimeSpan.FromSeconds(10)), result => Assert.Equal(OrderDecisionResult.Unavailable, result));
            var qualificationError = await Assert.ThrowsAsync<HttpRequestException>(() => qualification.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, qualificationError.StatusCode);
            var error = await Assert.ThrowsAsync<HttpRequestException>(() => permission.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
            Assert.Equal(1, login.Requests);
            Assert.Empty(downstream.Bearers);
            Assert.Equal(OrderDecisionResult.Completed, await order.TransitionAsync(115, true, "workload-recovered", CancellationToken.None));
            Assert.Equal(2, login.Requests);
            login.Release.TrySetResult();
            await login.LateContent.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, login.LateContent.Disposals);
            Assert.Equal(OrderDecisionResult.Completed, await order.TransitionAsync(115, true, "workload-cached", CancellationToken.None));
            Assert.Equal(2, login.Requests);
            Assert.All(downstream.Bearers, bearer => Assert.Equal("fresh-2", bearer));
        }
        finally { login.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Canceling_one_consumer_waiter_does_not_cancel_shared_owner_or_other_consumers(bool stallHeaders)
    {
        var login = new Login(stallHeaders);
        var downstream = new Downstream();
        await using var app = App(login, downstream, new FakeTimeProvider(), TimeSpan.FromSeconds(10));
        using var bootstrap = app.CreateClient();
        using var canceled = new CancellationTokenSource();
        var order = app.Services.GetRequiredService<IOrderDecisionClient>();
        var caller = order.TransitionAsync(115, true, "workload-canceled", canceled.Token);
        var survivor = order.TransitionAsync(116, true, "workload-survivor", CancellationToken.None);
        try
        {
            await login.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(login.OwnerCancellation.IsCancellationRequested);
            login.Release.TrySetResult();
            Assert.Equal(OrderDecisionResult.Completed, await survivor.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("late-1", Assert.Single(downstream.Bearers));
            Assert.Equal(1, login.Requests);
            Assert.Equal(OrderDecisionResult.Completed, await order.TransitionAsync(117, true, "workload-cache", CancellationToken.None));
            Assert.Equal(1, login.Requests);
        }
        finally { login.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Cached_workload_is_shared_across_scopes_and_consumers_then_refreshed_at_skew_boundary()
    {
        var time = new FakeTimeProvider();
        var login = new Login();
        var downstream = new Downstream();
        await using var app = App(login, downstream, time);
        using var bootstrap = app.CreateClient();
        var order = app.Services.GetRequiredService<IOrderDecisionClient>();
        Assert.Equal(OrderDecisionResult.Completed, await order.TransitionAsync(115, true, "cached-one", CancellationToken.None));
        using (var scope = app.Services.CreateScope())
        {
            using var authority = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("QualificationAuthority");
            using var response = await CheckAuthority(authority);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        time.Advance(TimeSpan.FromSeconds(239));
        Assert.Equal(OrderDecisionResult.Completed, await order.TransitionAsync(116, true, "cached-before", CancellationToken.None));
        Assert.Equal(1, login.Requests);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(OrderDecisionResult.Completed, await order.TransitionAsync(117, true, "cached-after", CancellationToken.None));
        Assert.Equal(2, login.Requests);
        Assert.Equal(new[] { "fresh-1", "fresh-1", "fresh-1", "fresh-2" }, downstream.Bearers.ToArray());
    }

    [Fact]
    public async Task Rejected_workload_token_is_invalidated_without_replaying_the_mutation()
    {
        var login = new Login();
        var downstream = new Downstream(rejectFirst: true);
        await using var app = App(login, downstream, new FakeTimeProvider());
        using var bootstrap = app.CreateClient();
        var order = app.Services.GetRequiredService<IOrderDecisionClient>();
        Assert.Equal(OrderDecisionResult.Unavailable, await order.TransitionAsync(115, true, "rejected-once", CancellationToken.None));
        Assert.Equal("fresh-1", Assert.Single(downstream.Bearers));
        Assert.Equal(1, login.Requests);
        Assert.Equal(OrderDecisionResult.Completed, await order.TransitionAsync(115, true, "explicit-retry", CancellationToken.None));
        Assert.Equal(2, login.Requests);
        Assert.Equal(new[] { "fresh-1", "fresh-2" }, downstream.Bearers.ToArray());
    }

    [Fact]
    public async Task Independent_normal_hosts_do_not_share_cached_workload_identity()
    {
        var leftLogin = new Login();
        var rightLogin = new Login();
        await using var left = App(leftLogin, new(), new FakeTimeProvider());
        await using var right = App(rightLogin, new(), new FakeTimeProvider());
        using var leftBootstrap = left.CreateClient();
        using var rightBootstrap = right.CreateClient();
        var leftProvider = left.Services.GetRequiredService<ILegacyServiceAccessTokenProvider>();
        var rightProvider = right.Services.GetRequiredService<ILegacyServiceAccessTokenProvider>();
        Assert.NotSame(leftProvider, rightProvider);
        Assert.Equal("fresh-1", await leftProvider.GetAccessTokenAsync());
        Assert.Equal("fresh-1", await rightProvider.GetAccessTokenAsync());
        Assert.Equal(1, leftLogin.Requests);
        Assert.Equal(1, rightLogin.Requests);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("wrongcase")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("oversized")]
    public async Task Invalid_login_body_fails_closed_without_downstream_send_and_allows_fresh_retry(string invalid)
    {
        var login = new Login(invalid: invalid);
        var downstream = new Downstream();
        await using var app = App(login, downstream, new FakeTimeProvider());
        using var bootstrap = app.CreateClient();
        var order = app.Services.GetRequiredService<IOrderDecisionClient>();
        Assert.Equal(OrderDecisionResult.Unavailable, await order.TransitionAsync(115, true, "invalid-login", CancellationToken.None));
        Assert.Empty(downstream.Bearers);
        Assert.Equal(1, login.Requests);
        Assert.Equal(OrderDecisionResult.Completed, await order.TransitionAsync(115, true, "fresh-login", CancellationToken.None));
        Assert.Equal(2, login.Requests);
        Assert.Equal("fresh-2", Assert.Single(downstream.Bearers));
    }

    [Fact]
    public async Task Registered_IAM_cache_is_shared_within_host_but_never_across_independent_normal_hosts()
    {
        var allowed = new Downstream();
        var denied = new Downstream { PermissionAllowed = false };
        await using var left = App(new(), allowed, new FakeTimeProvider());
        await using var right = App(new(), denied, new FakeTimeProvider());
        using var leftBootstrap = left.CreateClient();
        using var rightBootstrap = right.CreateClient();
        using (var first = left.Services.CreateScope())
            Assert.True(await first.ServiceProvider.GetRequiredService<IIamServiceClient>()
                .CheckPermissionAsync("source-cache-115", "legacy.quotations.read", "/quotations/115"));
        using (var second = left.Services.CreateScope())
            Assert.True(await second.ServiceProvider.GetRequiredService<IIamServiceClient>()
                .CheckPermissionAsync("source-cache-115", "legacy.quotations.read", "/quotations/115"));
        using (var isolated = right.Services.CreateScope())
            Assert.False(await isolated.ServiceProvider.GetRequiredService<IIamServiceClient>()
                .CheckPermissionAsync("source-cache-115", "legacy.quotations.read", "/quotations/115"));
        Assert.False(Assert.Single(allowed.PermissionBypass));
        Assert.False(Assert.Single(denied.PermissionBypass));
    }

    [Fact]
    public async Task Registered_IAM_live_check_bypasses_cached_allow_and_keeps_workload_and_live_credentials()
    {
        var downstream = new Downstream();
        await using var app = App(new(), downstream, new FakeTimeProvider());
        using var bootstrap = app.CreateClient();
        using var scope = app.Services.CreateScope();
        var iam = scope.ServiceProvider.GetRequiredService<IIamServiceClient>();
        Assert.True(await iam.CheckPermissionAsync("source-live-115", "legacy.quotations.update", "/quotations/115"));
        downstream.PermissionAllowed = false;
        Assert.False(await iam.CheckPermissionLiveAsync("source-live-115", "legacy.quotations.update", "/quotations/115"));
        Assert.Equal(new[] { false, true }, downstream.PermissionBypass.ToArray());
        Assert.All(downstream.Bearers, bearer => Assert.Equal("fresh-1", bearer));
        Assert.True(downstream.LiveCredentialPresent);
    }

    private static Task<HttpResponseMessage> CheckAuthority(HttpClient client) => client.PostAsJsonAsync(
        "/auth/v1/introspection/quotation-qualification",
        new { employeeAccessToken = "synthetic-employee", permission = "legacy.quotation-requests.read", purpose = QualificationAuthorityAttribute.Purpose, requestId = 115 });

    private WebApplicationFactory<Program> App(Login login, Downstream downstream, FakeTimeProvider time, TimeSpan? timeout = null)
        => fixture.App(new()).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.AddHttpClient(LegacyServiceAccessTokenProvider.HttpClientName, client => client.Timeout = timeout ?? TimeSpan.FromSeconds(1));
            services.PostConfigureAll<HttpClientFactoryOptions>(options => options.HttpMessageHandlerBuilderActions.Add(http =>
                http.PrimaryHandler = http.Name == LegacyServiceAccessTokenProvider.HttpClientName ? login : downstream));
        }));

    private sealed class Login : HttpMessageHandler
    {
        private readonly bool? stallHeaders;
        private readonly string? invalid;
        private int requests;
        public int Requests => Volatile.Read(ref requests);
        public CancellationToken OwnerCancellation { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TrackedContent LateContent { get; }
        public Login(bool? stallHeaders = null, string? invalid = null)
        {
            this.stallHeaders = stallHeaders;
            this.invalid = invalid;
            LateContent = new(new DeferredBody(Release.Task, stallHeaders == true));
        }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/auth/v1/service/login", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(new[] { "clientId", "clientSecret" }, body.RootElement.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));
            Assert.Equal("legacy-quotation", body.RootElement.GetProperty("clientId").GetString());
            var attempt = Interlocked.Increment(ref requests);
            if (attempt == 1 && stallHeaders is not null)
            {
                OwnerCancellation = cancellationToken;
                Entered.TrySetResult();
                if (stallHeaders.Value) await Release.Task;
                return new(HttpStatusCode.OK) { Content = LateContent };
            }
            var json = attempt == 1 ? invalid switch
            {
                "malformed" => "{",
                "wrongcase" => "{\"AccessToken\":\"invalid\",\"ExpiresIn\":300}",
                "short" => "{\"accessToken\":\"invalid\",\"expiresIn\":59}",
                "long" => "{\"accessToken\":\"invalid\",\"expiresIn\":3601}",
                "oversized" => new string('x', 32769),
                _ => $"{{\"accessToken\":\"fresh-{attempt}\",\"expiresIn\":300}}",
            } : $"{{\"accessToken\":\"fresh-{attempt}\",\"expiresIn\":300}}";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Downstream(bool rejectFirst = false) : HttpMessageHandler
    {
        public ConcurrentQueue<string?> Bearers { get; } = new();
        public ConcurrentQueue<bool> PermissionBypass { get; } = new();
        public bool PermissionAllowed { get; set; } = true;
        public bool LiveCredentialPresent { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Bearers.Enqueue(request.Headers.Authorization?.Parameter);
            if (rejectFirst && Bearers.Count == 1) return new(HttpStatusCode.Unauthorized);
            if (request.RequestUri!.AbsolutePath == "/iam/v1/auth/check-permission")
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var bypass = body.RootElement.GetProperty("bypassCache").GetBoolean();
                PermissionBypass.Enqueue(bypass);
                if (bypass) LiveCredentialPresent = request.Headers.TryGetValues("X-Maliev-IAM-Live-Check-Key", out var credentials)
                    && !string.IsNullOrWhiteSpace(Assert.Single(credentials));
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed = PermissionAllowed }) };
            }
            if (request.RequestUri!.AbsolutePath == "/auth/v1/introspection/quotation-qualification")
                return new(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { allowed = true, subject = "employee-42", permission = "legacy.quotation-requests.read", purpose = QualificationAuthorityAttribute.Purpose, requestId = 115 }),
                };
            return new(HttpStatusCode.Created);
        }
    }

    private sealed class TrackedContent(Stream stream) : StreamContent(stream)
    {
        private int disposals;
        public int Disposals => Volatile.Read(ref disposals);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void Dispose(bool disposing)
        {
            if (disposing) Interlocked.Increment(ref disposals);
            base.Dispose(disposing);
            if (disposing) Disposed.TrySetResult();
        }
    }

    private sealed class DeferredBody(Task release, bool immediate) : Stream
    {
        private readonly byte[] bytes = Encoding.UTF8.GetBytes("{\"accessToken\":\"late-1\",\"expiresIn\":300}");
        private int offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!immediate) await release;
            var count = Math.Min(buffer.Length, bytes.Length - offset);
            bytes.AsMemory(offset, count).CopyTo(buffer);
            offset += count;
            return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
