using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Normal Production Program/RS256/DI with disposable stores and a controlled external HTTP transport.
/// No IAM client, authorization handler or employee validator is replaced. This is not live Auth acceptance.</summary>
public sealed class QualificationBridgeConsumerHttpTests(QualificationBridgeConsumerFixture fixture)
    : IClassFixture<QualificationBridgeConsumerFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledAuthority_ValidEmployee_ReachesBothPositiveIdRoutes(bool update)
    {
        var seed = await fixture.SeedAsync();
        var employeeToken = fixture.EmployeeToken();
        var transport = new QualificationAuthorityTransport(employeeToken, fixture.WorkloadToken);
        await using var app = fixture.App(true, transport);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seed.Id, update, employeeToken);
        AssertNoCache(response);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            await using var denied = fixture.RequestContext();
            var unchanged = await denied.Requests.AsNoTracking().SingleAsync(x => x.Id == seed.Id);
            Assert.Equal(0, unchanged.QualificationVersion);
            Assert.Equal(seed.ModifiedDate, unchanged.ModifiedDate);
            Assert.Empty(await denied.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
        }
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(seed.Id, receipt.RootElement.GetProperty("RequestId").GetInt32());
        await using var db = fixture.RequestContext();
        var stored = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == seed.Id);
        var events = await db.RequestQualificationAudit.AsNoTracking().Where(x => x.RequestId == seed.Id).ToListAsync();
        Assert.Equal(update ? 1 : 0, stored.QualificationVersion);
        if (update) Assert.Equal("employee-42", Assert.Single(events).ChangedBy);
        else Assert.Empty(events);
        Assert.Equal(update ? "legacy.quotation-requests.update" : "legacy.quotation-requests.read", transport.Permission);
        Assert.Equal(seed.Id, transport.RequestId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledAuthority_SignedEmployeePermissions_DoNotAuthorizeOrWrite(bool update)
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken();
        await using var app = fixture.App(false, new QualificationAuthorityTransport(token, fixture.WorkloadToken));
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seed.Id, update, token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await using var db = fixture.RequestContext();
        var stored = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == seed.Id);
        Assert.Equal(0, stored.QualificationVersion);
        Assert.Equal(seed.ModifiedDate, stored.ModifiedDate);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
    }

    [Theory]
    [InlineData("wrong-subject")]
    [InlineData("wrong-permission")]
    [InlineData("wrong-purpose")]
    [InlineData("wrong-id")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("oversized")]
    [InlineData("unavailable")]
    [InlineData("rate-limit")]
    [InlineData("streamed-oversized")]
    [InlineData("null-subject")]
    [InlineData("wrong-type")]
    [InlineData("trailing")]
    [InlineData("false-with-subject")]
    [InlineData("broken-stream")]
    public async Task EnabledAuthority_UntrustedOrUnavailableResponse_NoWrite503(string scenario)
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken();
        await using var app = fixture.App(true, new QualificationAuthorityTransport(token, fixture.WorkloadToken, scenario));
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seed.Id, true, token);
        await using var db = fixture.RequestContext();
        var stored = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == seed.Id);
        Assert.Equal(0, stored.QualificationVersion);
        Assert.Equal(seed.ModifiedDate, stored.ModifiedDate);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        AssertNoCache(response);
        Assert.Null(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task EnabledDeny_CannotFallBackToNormallyConfiguredAllowedLegacyIam()
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken();
        var iam = new AllowedIamTransport();
        await using (var disabled = fixture.App(false, new QualificationAuthorityTransport(token, fixture.WorkloadToken), iam))
        {
            using var client = disabled.CreateClient();
            using var response = await SendAsync(client, seed.Id, false, token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        Assert.Equal(1, iam.Calls);
        var enabledIam = new AllowedIamTransport();
        await using (var enabled = fixture.App(true, new QualificationAuthorityTransport(token, fixture.WorkloadToken, "deny"), enabledIam))
        {
            using var client = enabled.CreateClient();
            using var response = await SendAsync(client, seed.Id, true, token);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        Assert.Equal(0, enabledIam.Calls);
        await using var db = fixture.RequestContext();
        Assert.Equal(0, (await db.Requests.SingleAsync(x => x.Id == seed.Id)).QualificationVersion);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
    }

    [Fact]
    public async Task SlowAuthorityBody_HasOwnTenSecondDeadline_WithoutCallerAbort()
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken();
        await using var app = fixture.App(true, new QualificationAuthorityTransport(token, fixture.WorkloadToken, "slow-body"));
        using var client = app.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await SendAsync(client, seed.Id, true, token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var db = fixture.RequestContext();
        Assert.Equal(0, (await db.Requests.SingleAsync(x => x.Id == seed.Id)).QualificationVersion);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
    }

    [Fact]
    public async Task StreamedExact4096Response_StillAllowsWithoutDeclaredLength()
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken();
        await using var app = fixture.App(true, new QualificationAuthorityTransport(token, fixture.WorkloadToken, "streamed-boundary"));
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seed.Id, false, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SameKeyReplay_RechecksLiveAuthority_NoPositiveDecisionCache()
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken();
        var authority = new QualificationAuthorityTransport(token, fixture.WorkloadToken);
        await using var app = fixture.App(true, authority);
        using var client = app.CreateClient();
        using (var first = await SendAsync(client, seed.Id, true, token)) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        authority.DenyNext = true;
        using (var second = await SendAsync(client, seed.Id, true, token)) Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        Assert.Equal(2, authority.Calls);
        await using var db = fixture.RequestContext();
        Assert.Equal(1, (await db.Requests.SingleAsync(x => x.Id == seed.Id)).QualificationVersion);
        Assert.Single(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("service")]
    [InlineData("conflicting-alias")]
    [InlineData("oversized-token")]
    public async Task InvalidEmployeeInput_NoRemoteAdmissionAndNoWrite(string profile)
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken(profile);
        var authority = new QualificationAuthorityTransport(token, fixture.WorkloadToken);
        await using var app = fixture.App(true, authority);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seed.Id, true, token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, authority.Calls);
        await using var db = fixture.RequestContext();
        Assert.Equal(0, (await db.Requests.SingleAsync(x => x.Id == seed.Id)).QualificationVersion);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
    }

    [Fact]
    public async Task CallerCancellation_PropagatesAndCannotCommit()
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken();
        var authority = new QualificationAuthorityTransport(token, fixture.WorkloadToken, "cancel-header");
        await using var app = fixture.App(true, authority);
        using var client = app.CreateClient();
        using var abort = new CancellationTokenSource();
        var pending = SendAsync(client, seed.Id, true, token, abort.Token);
        await authority.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        abort.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await authority.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await using var db = fixture.RequestContext();
        Assert.Equal(0, (await db.Requests.SingleAsync(x => x.Id == seed.Id)).QualificationVersion);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("wrong-issuer")]
    public async Task NormalJwtRejection_Remains401BeforeAuthority(string profile)
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken(profile);
        var authority = new QualificationAuthorityTransport(token, fixture.WorkloadToken);
        await using var app = fixture.App(true, authority);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seed.Id, true, token);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, authority.Calls);
        await using var db = fixture.RequestContext();
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
    }

    [Theory]
    [InlineData("/quotationrequests")]
    [InlineData("/quotationrequests/qualification-outcomes/readback?fromUtc=2026-09-30T00:00:00Z&toUtc=2026-10-01T00:00:00Z")]
    public async Task OtherAndAggregateRoutes_NeverUseQualificationBridge(string path)
    {
        var token = fixture.EmployeeToken();
        var authority = new QualificationAuthorityTransport(token, fixture.WorkloadToken);
        await using var app = fixture.App(true, authority);
        using var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, authority.Calls);
    }

    [Theory]
    [InlineData("caller401")]
    [InlineData("caller403")]
    [InlineData("deny")]
    public async Task AuthorityAuthenticationOrDecisionDenial_Is403WithoutMutation(string scenario)
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken();
        var authority = new QualificationAuthorityTransport(token, fixture.WorkloadToken, scenario);
        await using var app = fixture.App(true, authority);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seed.Id, true, token);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, authority.Calls);
        AssertNoCache(response);
        await using var db = fixture.RequestContext();
        var stored = await db.Requests.SingleAsync(x => x.Id == seed.Id);
        Assert.Equal(seed.ModifiedDate, stored.ModifiedDate);
        Assert.Equal(0, stored.QualificationVersion);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task NonpositiveResourceId_Is400BeforeAuthority(int id)
    {
        var token = fixture.EmployeeToken();
        var authority = new QualificationAuthorityTransport(token, fixture.WorkloadToken);
        await using var app = fixture.App(true, authority);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, id, true, token);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, authority.Calls);
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task RealPrimaryHandler_RedirectCannotForwardEmployeeBodyToSecondOrigin(int status)
    {
        var seed = await fixture.SeedAsync();
        var token = fixture.EmployeeToken();
        var leaked = false;
        var sinkBuilder = WebApplication.CreateBuilder();
        sinkBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        sinkBuilder.Logging.ClearProviders();
        await using var sink = sinkBuilder.Build();
        sink.MapPost("/capture", async (HttpRequest request) =>
        {
            using var reader = new StreamReader(request.Body);
            var body = await reader.ReadToEndAsync();
            leaked = body.Contains(token, StringComparison.Ordinal);
            return Results.Json(new
            {
                allowed = false,
                subject = (string?)null,
                permission = "legacy.quotation-requests.update",
                purpose = "quotation-request-qualification",
                requestId = seed.Id
            });
        });
        await sink.StartAsync();
        var sinkOrigin = sink.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var redirectBuilder = WebApplication.CreateBuilder();
        redirectBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        redirectBuilder.Logging.ClearProviders();
        await using var redirect = redirectBuilder.Build();
        redirect.MapPost("/auth/v1/introspection/quotation-qualification", (HttpContext context) =>
        {
            context.Response.StatusCode = status;
            context.Response.Headers.Location = sinkOrigin + "/capture";
        });
        await redirect.StartAsync();
        var origin = redirect.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        // No named authority transport override: exercise actual runtime primary HTTP handler/redirect behavior.
        await using var app = fixture.App(true, null, authOrigin: origin);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seed.Id, true, token);
        Assert.False(leaked, "A redirect must never deliver the employee JWT body to another origin.");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var db = fixture.RequestContext();
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seed.Id).ToListAsync());
    }

    private static void AssertNoCache(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore == true);
        Assert.Contains(response.Headers.Pragma, header => header.Name == "no-cache");
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, int id, bool update, string token, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(update ? HttpMethod.Put : HttpMethod.Get,
            $"/quotationrequests/{id}/{(update ? "qualification" : "qualification-receipt")}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (update) request.Content = JsonContent.Create(new QualificationStateUpdateRequest(
            "qualified", null, "complete", 0, null, "bridge-review-1", 0));
        return client.SendAsync(request, cancellationToken);
    }
}

/// <summary>Controlled remote HTTP contract, never a local authority or IAM replacement.</summary>
public sealed class QualificationAuthorityTransport(string expectedEmployeeToken, string expectedWorkloadToken, string scenario = "allow") : HttpMessageHandler
{
    public string? Permission { get; private set; }
    public int RequestId { get; private set; }
    public int Calls { get; private set; }
    public bool DenyNext { get; set; }
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        if (scenario == "cancel-header")
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
        }
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/auth/v1/introspection/quotation-qualification", request.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.False(string.IsNullOrWhiteSpace(request.Headers.Authorization?.Parameter));
        Assert.NotEqual(expectedEmployeeToken, request.Headers.Authorization!.Parameter);
        Assert.Equal(expectedWorkloadToken, request.Headers.Authorization.Parameter);
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        var body = json.RootElement;
        Assert.Equal(4, body.EnumerateObject().Count());
        Assert.Equal(expectedEmployeeToken, body.GetProperty("employeeAccessToken").GetString());
        Assert.Equal("quotation-request-qualification", body.GetProperty("purpose").GetString());
        Permission = body.GetProperty("permission").GetString();
        RequestId = body.GetProperty("requestId").GetInt32();
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                allowed = true,
                subject = "employee-42",
                permission = Permission,
                purpose = "quotation-request-qualification",
                requestId = RequestId
            })
        };
        response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        if (scenario == "unavailable") response.StatusCode = HttpStatusCode.ServiceUnavailable;
        if (scenario == "caller401") response.StatusCode = HttpStatusCode.Unauthorized;
        if (scenario == "caller403") response.StatusCode = HttpStatusCode.Forbidden;
        if (scenario == "rate-limit") { response.StatusCode = HttpStatusCode.TooManyRequests; response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1)); }
        if (scenario == "deny" || DenyNext) response.Content = JsonContent.Create(new { allowed = false, subject = (string?)null, permission = Permission, purpose = "quotation-request-qualification", requestId = RequestId });
        if (scenario is "slow-body" or "broken-stream") response.Content = new SlowBodyContent(scenario == "broken-stream");
        if (scenario is not ("allow" or "unavailable" or "rate-limit" or "deny" or "slow-body" or "broken-stream" or "caller401" or "caller403"))
        {
            var subject = scenario == "wrong-subject" ? "employee-other" : "employee-42";
            var permission = scenario == "wrong-permission" ? "legacy.quotation-requests.read" : Permission;
            var purpose = scenario == "wrong-purpose" ? "invoice-delegation" : "quotation-request-qualification";
            var id = scenario == "wrong-id" ? RequestId + 1 : RequestId;
            var raw = JsonSerializer.Serialize(new { allowed = true, subject, permission, purpose, requestId = id });
            if (scenario == "missing") raw = "{}";
            if (scenario == "duplicate") raw = raw.Insert(1, "\"allowed\":false,");
            if (scenario == "extra") raw = raw.Insert(1, "\"actor\":\"injected\",");
            if (scenario == "oversized") raw += new string(' ', 4097);
            if (scenario == "null-subject") raw = raw.Replace("\"employee-42\"", "null", StringComparison.Ordinal);
            if (scenario == "wrong-type") raw = raw.Replace("\"allowed\":true", "\"allowed\":\"true\"", StringComparison.Ordinal);
            if (scenario == "false-with-subject") raw = raw.Replace("\"allowed\":true", "\"allowed\":false", StringComparison.Ordinal);
            if (scenario == "trailing") raw += "{}";
            response.Content = new StringContent(raw, Encoding.UTF8, "application/json");
            if (scenario is "streamed-boundary" or "streamed-oversized")
                response.Content = new UnknownLengthContent(Encoding.UTF8.GetBytes(raw.PadRight(scenario == "streamed-boundary" ? 4096 : 4097)));
        }
        return response;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new MemoryStream(bytes));
    }

    private sealed class SlowBodyContent(bool broken = false) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new SlowStream(broken));
    }
    private sealed class SlowStream(bool broken) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { if (broken) throw new IOException("Synthetic response body interruption."); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class AllowedIamTransport : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri!.AbsolutePath);
        Assert.Equal("synthetic-live-check-only", Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        Assert.Equal("employee-42", json.RootElement.GetProperty("principalId").GetString());
        Assert.True(json.RootElement.GetProperty("bypassCache").GetBoolean());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed = true }) };
    }
}

/// <summary>Fixture-owned keys, PostgreSQL18, Redis and ordinary configured application.</summary>
public sealed class QualificationBridgeConsumerFixture : IAsyncLifetime
{
    private Infrastructure.DisposableContainerPair? containers;
    private PostgreSqlContainer postgres => (PostgreSqlContainer)containers!.First;
    private IContainer redis => containers!.Second;
    private readonly RSA key = RSA.Create(2048);
    private string? workloadToken;
    public string WorkloadToken => workloadToken ??= IssueToken([new("sub", "service:legacy-quotation"), new("identity_kind", "service"),
        new("permissions", "legacy-auth.quotation-qualification.introspect")]);
    private string RequestConnection => new NpgsqlConnectionStringBuilder(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())) { Database = "quotation76_requests" }.ConnectionString;

    public async Task InitializeAsync()
    {
        containers = await Infrastructure.DisposableContainerPair.StartAsync("quotation99-qualification-bridge",
            attempt => new PostgreSqlBuilder("postgres:18-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels)
                .WithCreateParameterModifier(parameters => ConfigureLoopback(parameters, "5432/tcp")).Build(),
            attempt => new ContainerBuilder("redis:7-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
                .WithCreateParameterModifier(parameters => ConfigureLoopback(parameters, "6379/tcp")).Build());
        await using (var connection = new NpgsqlConnection(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE quotation76_requests", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var quotation = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())).Options);
        await quotation.Database.MigrateAsync();
        await using var requests = RequestContext();
        await requests.Database.MigrateAsync();
    }

    public QuotationRequestDbContext RequestContext() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(RequestConnection).Options);

    public async Task<QuotationRequest> SeedAsync()
    {
        await using var db = RequestContext();
        var request = new QuotationRequest
        {
            JourneyId = Guid.NewGuid(),
            CreatedDate = new DateTime(2026, 10, 1),
            ModifiedDate = new DateTime(2026, 10, 1),
            QualificationState = "unreviewed",
            QualificationVersion = 0
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        request.TransactionId = $"request-{request.Id}";
        await db.SaveChangesAsync();
        return request;
    }

    public string EmployeeToken(string profile = "employee") => IssueToken([new Claim("sub", profile == "service" ? "service:legacy-intranet" : "employee-42"), new Claim("identity_kind", profile is "customer" or "service" ? profile : "employee"),
            new Claim("sid", "2f7941d5-929f-49ab-acec-7f4b9c463d25"),
            new Claim("permissions", "legacy.quotation-requests.read"), new Claim("permissions", "legacy.quotation-requests.update"),
            new Claim(profile == "conflicting-alias" ? "user_id" : "fixture-padding", profile == "oversized-token" ? new string('x', 13000) : "other")], profile);

    private string IssueToken(Claim[] claims, string profile = "") => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        issuer: profile == "wrong-issuer" ? "https://wrong-issuer.example" : "https://fixture-auth.example", audience: "fixture-services",
        claims: claims,
        notBefore: DateTime.UtcNow.AddMinutes(-10), expires: DateTime.UtcNow.AddMinutes(profile == "expired" ? -6 : 5),
        signingCredentials: new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));

    public WebApplicationFactory<Program> App(bool enabled, HttpMessageHandler? transport, AllowedIamTransport? iam = null, string? authOrigin = null) => new Factory(new Dictionary<string, string?>
    {
        ["ConnectionStrings:QuotationDbContext"] = Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString()),
        ["ConnectionStrings:QuotationRequestDbContext"] = RequestConnection,
        ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}",
        ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())),
        ["Jwt:Issuer"] = "https://fixture-auth.example",
        ["Jwt:Audience"] = "fixture-services",
        ["QualificationAuthority:Enabled"] = enabled.ToString(),
        ["Services:Auth:BaseUrl"] = authOrigin ?? "https://fixture-auth.example",
        ["ServiceAuthentication:ClientId"] = "legacy-quotation",
        ["ServiceAuthentication:ClientSecret"] = "synthetic-fixture-only-secret",
        ["IAM:LivePermissionChecks:Credential"] = "synthetic-live-check-only",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        ["Observability:RuntimeMetricsEnabled"] = "false"
    }, transport, WorkloadToken, iam);

    public async Task DisposeAsync()
    {
        try { if (containers is not null) await containers.DisposeAsync(); }
        finally { key.Dispose(); }
    }

    private static void ConfigureLoopback(Docker.DotNet.Models.CreateContainerParameters parameters, string port)
    {
        parameters.HostConfig ??= new Docker.DotNet.Models.HostConfig();
        parameters.HostConfig.PortBindings ??= new Dictionary<string, IList<Docker.DotNet.Models.PortBinding>>();
        parameters.HostConfig.PortBindings[port] = [new Docker.DotNet.Models.PortBinding { HostIP = "127.0.0.1", HostPort = "" }];
    }

    private sealed class Factory(Dictionary<string, string?> settings, HttpMessageHandler? transport, string workloadToken, AllowedIamTransport? iam) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                if (transport is not null)
                    services.Configure<HttpClientFactoryOptions>("QualificationAuthority",
                        options => options.HttpMessageHandlerBuilderActions.Add(http => http.PrimaryHandler = transport));
                services.Configure<HttpClientFactoryOptions>(LegacyServiceAccessTokenProvider.HttpClientName,
                    options => options.HttpMessageHandlerBuilderActions.Add(http => http.PrimaryHandler = new ServiceLoginTransport(workloadToken)));
                if (iam is not null)
                {
                    // Actual pinned client and normal permission handler; only remote transport is controlled.
                    services.AddScoped<IIamServiceClient, IamServiceClient>();
                    services.AddHttpClient("IAMService", client => client.BaseAddress = new Uri("https://fixture-iam.example"))
                        .ConfigurePrimaryHttpMessageHandler(() => iam);
                }
            });
        }
    }

    private sealed class ServiceLoginTransport(string token) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/auth/v1/service/login", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
            Assert.Equal("legacy-quotation", json.RootElement.GetProperty("clientId").GetString());
            Assert.Equal("synthetic-fixture-only-secret", json.RootElement.GetProperty("clientSecret").GetString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { accessToken = token, expiresIn = 300 }) };
        }
    }
}
