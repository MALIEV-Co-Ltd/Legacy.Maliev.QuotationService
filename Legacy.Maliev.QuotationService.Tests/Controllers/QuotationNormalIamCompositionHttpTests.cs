using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Maliev.Aspire.ServiceDefaults;
using Maliev.Aspire.ServiceDefaults.IAM;
using Maliev.Aspire.ServiceDefaults.LegacyAuth;
using Microsoft.AspNetCore.Hosting;
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

/// <summary>Normal Program composition, real RS256 validation and disposable PostgreSQL; external HTTP only is controlled.</summary>
public sealed class QuotationNormalIamCompositionHttpTests(QuotationNormalIamFixture fixture)
    : IClassFixture<QuotationNormalIamFixture>
{
    [Fact]
    public void NamedIamOrigin_AbsentConfigurationRetainsBoundedLogicalDiscoveryWithoutGrant()
    {
        using var app = fixture.App(new(), origin: null);
        using var bootstrap = app.CreateClient();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");
        Assert.Equal("https+http://iamservice/", client.BaseAddress?.AbsoluteUri);
    }

    [Theory]
    [InlineData("Production", "https://quotation95-iam.invalid", "https://quotation95-iam.invalid/")]
    [InlineData("Production", "https+http://IAMService", "https+http://iamservice/")]
    [InlineData("Production", "https+http://LEGACY-MALIEV-IAM-SERVICE", "https+http://legacy-maliev-iam-service/")]
    [InlineData("Testing", "http://127.0.0.1:12345", "http://127.0.0.1:12345/")]
    [InlineData("Development", "http://[::1]:12345", "http://[::1]:12345/")]
    public void NamedIamOrigin_AllowedCanonicalOriginUsesNormalComposition(string environment, string origin, string expected)
    {
        using var app = fixture.App(new(), origin: origin, environment: environment);
        using var bootstrap = app.CreateClient();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");
        Assert.Equal(expected, client.BaseAddress?.AbsoluteUri);
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
    }

    [Theory]
    [InlineData("Production", "http://127.0.0.1:12345")]
    [InlineData("Production", "http://quotation95-iam.invalid")]
    [InlineData("Testing", "http://remote.invalid")]
    [InlineData("Development", "http://0.0.0.0:12345")]
    [InlineData("Production", "https://user:pass@quotation95-iam.invalid")]
    [InlineData("Production", "https://quotation95-iam.invalid/?secret=value")]
    [InlineData("Production", "https://quotation95-iam.invalid/#fragment")]
    [InlineData("Production", "https://quotation95-iam.invalid/path")]
    [InlineData("Production", " https://quotation95-iam.invalid")]
    [InlineData("Production", "")]
    [InlineData("Production", "https+http://remote.invalid")]
    [InlineData("Production", "https+http://IAMService:12345")]
    public void NamedIamOrigin_UnsafeExplicitOriginIsRejectedBeforeTransport(string environment, string origin)
    {
        var boundary = new QuotationNormalIamBoundary();
        using var app = fixture.App(boundary, origin: origin, environment: environment);
        using var bootstrap = app.CreateClient();
        Assert.Throws<InvalidOperationException>(() => app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService"));
        Assert.Equal(0, boundary.IamCalls);
        Assert.Equal(0, boundary.LoginCalls);
    }

    [Fact]
    public void NamedIamPrimaryHandler_DisablesRedirectsBeforeAnyExternalSend()
    {
        var boundary = new QuotationNormalIamBoundary { InspectPrimaryOnly = true };
        using var app = fixture.App(boundary);
        using var bootstrap = app.CreateClient();
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("IAMService");
        Assert.True(boundary.PrimaryRedirectsDisabled);
        Assert.Equal(0, boundary.IamCalls);
        Assert.Equal(0, boundary.LoginCalls);
    }

    [Fact]
    public void ProductionProgram_ResolvesActualIamClientWithoutTestRegistration()
    {
        using var app = fixture.App(new());
        using var client = app.CreateClient();
        using var scope = app.Services.CreateScope();
        Assert.IsType<IamServiceClient>(scope.ServiceProvider.GetService<IIamServiceClient>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task AllowedLiveCheck_ReachesOrdinaryUpdateAndPreservesFinancialAndOriginFields(bool? accepted)
    {
        var row = await fixture.SeedAsync(accepted);
        var boundary = new QuotationNormalIamBoundary();
        using var app = fixture.App(boundary);
        using var client = fixture.Client(app);
        using var response = await UpdateAsync(client, row, promote: false);
        Assert.Equal(1, boundary.IamCalls);
        Assert.Equal(1, boundary.LoginCalls);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var db = fixture.Context();
        var stored = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id);
        Assert.Equal("updated normal composition note", stored.Comment);
        Assert.NotEqual(row.ModifiedDate, stored.ModifiedDate);
        AssertStable(row, stored);
        Assert.Empty(await db.AcceptedOutcomes.Where(value => value.QuotationId == row.Id).ToListAsync());
    }

    [Theory]
    [InlineData("denied")]
    [InlineData("unavailable")]
    [InlineData("network")]
    public async Task ReachedLiveFailure_RejectsWithoutMutation(string mode)
    {
        var row = await fixture.SeedAsync();
        var boundary = new QuotationNormalIamBoundary { Mode = mode };
        using var app = fixture.App(boundary);
        using var client = fixture.Client(app);
        using var response = await UpdateAsync(client, row, promote: false);
        Assert.Equal(1, boundary.IamCalls);
        Assert.Equal(1, boundary.LoginCalls);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUnchangedAsync(row);
    }

    [Fact]
    public async Task MissingLiveCredential_ActualClientFailsClosedBeforeExternalSend()
    {
        var row = await fixture.SeedAsync();
        var boundary = new QuotationNormalIamBoundary();
        using var app = fixture.App(boundary, credential: "");
        using var client = fixture.Client(app);
        using var scope = app.Services.CreateScope();
        Assert.IsType<IamServiceClient>(scope.ServiceProvider.GetService<IIamServiceClient>());
        using var response = await UpdateAsync(client, row, promote: false);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, boundary.IamCalls);
        Assert.Equal(0, boundary.LoginCalls);
        await AssertUnchangedAsync(row);
    }

    [Fact]
    public async Task Cancellation_AfterActualIamEntryPreservesRow()
    {
        var row = await fixture.SeedAsync();
        var boundary = new QuotationNormalIamBoundary { Mode = "wait" };
        using var app = fixture.App(boundary);
        using var client = fixture.Client(app);
        using var cancel = new CancellationTokenSource();
        var pending = UpdateAsync(client, row, promote: false, cancel.Token);
        var first = await Task.WhenAny(boundary.Entered.Task, pending);
        Assert.Same(boundary.Entered.Task, first); // Early 403 is not a cancellation proof.
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var result = await pending; });
        Assert.Equal(1, boundary.IamCalls);
        await AssertUnchangedAsync(row);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public async Task HistoricalFullPutPromotion_ReachesConflictNotEarlyAuthorization(bool? accepted)
    {
        var row = await fixture.SeedAsync(accepted);
        var boundary = new QuotationNormalIamBoundary();
        using var app = fixture.App(boundary);
        using var client = fixture.Client(app);
        using var response = await UpdateAsync(client, row, promote: true);
        Assert.Equal(1, boundary.IamCalls);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(row);
    }

    [Fact]
    public async Task AccountingEmployeeIntent_IsStillRejectedAfterActualLiveAuthorization()
    {
        var row = await fixture.SeedAsync();
        var boundary = new QuotationNormalIamBoundary();
        using var app = fixture.App(boundary);
        using var client = fixture.Client(app);
        using var response = await client.PutAsJsonAsync($"/quotations/{row.Id}/decision",
            new { Accepted = true, EmployeeInitiated = true, InvoiceId = 901 });
        Assert.Equal(1, boundary.IamCalls);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUnchangedAsync(row);
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("wrong-audience")]
    [InlineData("wrong-issuer")]
    public async Task InvalidCaller_IsRejectedByNormalJwtBeforeExternalSend(string profile)
    {
        var row = await fixture.SeedAsync();
        var boundary = new QuotationNormalIamBoundary();
        using var app = fixture.App(boundary);
        using var client = fixture.Client(app, profile);
        using var response = await UpdateAsync(client, row, promote: false);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, boundary.IamCalls);
        Assert.Equal(0, boundary.LoginCalls);
        await AssertUnchangedAsync(row);
    }

    private static Task<HttpResponseMessage> UpdateAsync(HttpClient client, Quotation row, bool promote,
        CancellationToken cancellationToken = default) => client.PutAsJsonAsync($"/quotations/{row.Id}", new
        {
            row.CustomerId,
            row.EmployeeId,
            row.InvoiceId,
            row.CurrencyId,
            row.Period,
            row.ExpirationDate,
            row.Subtotal,
            row.Vat,
            row.Total,
            row.WithholdingTax,
            Comment = "updated normal composition note",
            Accepted = promote ? true : row.Accepted
        }, cancellationToken);

    private async Task AssertUnchangedAsync(Quotation row)
    {
        await using var db = fixture.Context();
        var stored = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id);
        AssertStable(row, stored);
        Assert.Equal(row.ModifiedDate, stored.ModifiedDate);
        Assert.Equal(row.Comment, stored.Comment);
        Assert.Empty(await db.AcceptedOutcomes.Where(value => value.QuotationId == row.Id).ToListAsync());
    }

    private static void AssertStable(Quotation row, Quotation stored)
    {
        Assert.Equal(row.Accepted, stored.Accepted);
        Assert.Equal(row.InvoiceId, stored.InvoiceId);
        Assert.Equal(row.CustomerId, stored.CustomerId);
        Assert.Equal(row.EmployeeId, stored.EmployeeId);
        Assert.Equal(row.Subtotal, stored.Subtotal);
        Assert.Equal(row.Vat, stored.Vat);
        Assert.Equal(row.Total, stored.Total);
        Assert.Equal(row.WithholdingTax, stored.WithholdingTax);
        Assert.Equal(row.CurrencyId, stored.CurrencyId);
        Assert.Equal(row.SourceJourneyId, stored.SourceJourneyId);
        Assert.Equal(row.SourceRequestId, stored.SourceRequestId);
        Assert.Null(stored.AcceptedUtc);
        Assert.Null(stored.AcceptanceOrigin);
    }
}

/// <summary>One class-owned, fresh local tmpfs database/cache; each case owns a unique quotation and normal host.</summary>
public sealed class QuotationNormalIamFixture : IAsyncLifetime
{
    private const string Issuer = "https://quotation95-auth.invalid";
    private const string Audience = "quotation95-services";
    private readonly RSA key = RSA.Create(2048);
    private readonly string liveCredential = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private PostgreSqlContainer? postgres;
    private IContainer? redis;
    private Infrastructure.DisposableContainerPair? containers;
    private string Requests => new NpgsqlConnectionStringBuilder(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres!.GetConnectionString())) { Database = "quotation95_requests", Pooling = false }.ConnectionString;
    public QuotationDbContext Context() => new(new DbContextOptionsBuilder<QuotationDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres!.GetConnectionString())) { Pooling = false }.ConnectionString).Options);

    public async Task InitializeAsync()
    {
        containers = await Infrastructure.DisposableContainerPair.StartAsync("quotation99-normal-iam",
            attempt => new PostgreSqlBuilder("postgres:18-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels)
                .WithDatabase("quotation95").WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)))
                .WithCreateParameterModifier(parameters => ConfigureOwnedStorage(parameters, "5432/tcp", "/var/lib/postgresql", 268435456)).Build(),
            attempt => new ContainerBuilder("redis:7-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels)
                .WithPortBinding(6379, true).WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379))
                .WithCreateParameterModifier(parameters => ConfigureOwnedStorage(parameters, "6379/tcp", "/data", 16777216)).Build());
        postgres = (PostgreSqlContainer)containers.First;
        redis = containers.Second;
        await using (var connection = new NpgsqlConnection(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE quotation95_requests", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var db = Context();
        await db.Database.MigrateAsync();
        await using var requests = new QuotationRequestDbContext(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(Requests).Options);
        await requests.Database.MigrateAsync();
    }

    private static void ConfigureOwnedStorage(CreateContainerParameters parameters, string port, string path, int bytes)
    {
        parameters.HostConfig ??= new HostConfig();
        parameters.HostConfig.PortBindings ??= new Dictionary<string, IList<PortBinding>>();
        parameters.HostConfig.PortBindings[port] = [new PortBinding { HostIP = "127.0.0.1", HostPort = "" }];
        parameters.HostConfig.Tmpfs = new Dictionary<string, string> { [path] = $"rw,noexec,nosuid,size={bytes}" };
    }

    public async Task<Quotation> SeedAsync(bool? accepted = null)
    {
        await using var db = Context();
        var row = new Quotation
        {
            CustomerId = 42,
            EmployeeId = 17,
            CurrencyId = 764,
            Period = 30,
            ExpirationDate = new DateTime(2035, 1, 1),
            Subtotal = 100m,
            Vat = 7m,
            Total = 107m,
            WithholdingTax = 3m,
            Accepted = accepted,
            Comment = "synthetic quotation95 note",
            CreatedDate = new DateTime(2026, 9, 29, 3, 4, 5),
            ModifiedDate = new DateTime(2026, 9, 29, 4, 4, 5),
            SourceRequestId = 17,
            SourceJourneyId = Guid.NewGuid()
        };
        db.Quotations.Add(row);
        await db.SaveChangesAsync();
        return row;
    }

    public WebApplicationFactory<Program> App(QuotationNormalIamBoundary boundary, string? credential = null,
        string? origin = "https://quotation95-iam.invalid", string environment = "Production")
    {
        boundary.ExpectedCredential = credential ?? liveCredential;
        return new Factory(this, boundary, boundary.ExpectedCredential, origin, environment);
    }

    public HttpClient Client(WebApplicationFactory<Program> app, string profile = "accounting")
    {
        var client = app.CreateClient();
        if (profile != "unsigned") client.DefaultRequestHeaders.Authorization = new("Bearer", Token("service:legacy-accounting", profile));
        return client;
    }

    private string Token(string subject, string profile = "accounting") => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        profile == "wrong-issuer" ? "https://untrusted.invalid" : Issuer,
        profile == "wrong-audience" ? "not-quotation95" : Audience,
        [new Claim("sub", subject), new Claim("identity_kind", "service"), new Claim("permissions", "legacy.quotations.update")],
        DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));

    // Fixture signer only: actual Auth financial-readback issuance belongs to the cross-project joined lane.
    internal string InvoiceCapability(int quotationId, int invoiceId, Guid operation, DateTime version, string mode = "bound", TimeProvider? clock = null)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var claims = new List<Claim>
        {
            new("sub", "employee-42"),
            new("jti", Guid.NewGuid().ToString("D")),
            new("iat", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new("azp", "service:legacy-intranet"),
            new("executor", "service:legacy-accounting"),
            new("scope", "legacy.quotation.invoice-complete"),
            new("quotation_id", quotationId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("operation_id", operation.ToString("D")),
        };
        if (mode != "unbound")
        {
            claims.Add(new("invoice_id", (mode == "other-invoice" ? invoiceId + 1 : invoiceId).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            claims.Add(new("quotation_version", DateTime.SpecifyKind(mode == "other-version" ? version.AddSeconds(1) : version, DateTimeKind.Utc).ToString("O")));
            claims.Add(new("financial_binding", new string(mode == "bad-binding" ? 'a' : 'A', 64)));
            claims.Add(new("financial_binding_version", "invoice-creation-financial-v1"));
        }
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer,
            "legacy-quotation:invoice-complete", claims, now.UtcDateTime, now.AddSeconds(60).UtcDateTime,
            new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
    }

    public async Task DisposeAsync()
    {
        try { if (containers is not null) await containers.DisposeAsync(); }
        finally { key.Dispose(); }
    }

    private sealed class Factory(QuotationNormalIamFixture fixture, QuotationNormalIamBoundary boundary,
        string credential, string? origin, string environment) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(environment);
            foreach (var setting in new Dictionary<string, string?>
            {
                ["ConnectionStrings:QuotationDbContext"] = new NpgsqlConnectionStringBuilder(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(fixture.postgres!.GetConnectionString())) { Pooling = false }.ConnectionString,
                ["ConnectionStrings:QuotationRequestDbContext"] = fixture.Requests,
                ["ConnectionStrings:redis"] = $"127.0.0.1:{fixture.redis!.GetMappedPublicPort(6379)}",
                ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(fixture.key.ExportSubjectPublicKeyInfoPem())),
                ["Jwt:Issuer"] = Issuer,
                ["Jwt:Audience"] = Audience,
                ["Services:Auth:BaseUrl"] = Issuer,
                ["Services:IAM:BaseUrl"] = origin,
                ["Services:Order:BaseUrl"] = "https://quotation95-order.invalid",
                ["ServiceAuthentication:ClientId"] = "legacy-quotation",
                ["ServiceAuthentication:ClientSecret"] = "synthetic-quotation95-secret",
                ["IAM:LivePermissionChecks:Credential"] = credential,
                ["QualificationAuthority:Enabled"] = "false",
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
                ["Observability:RuntimeMetricsEnabled"] = "false"
            })
            {
                // UseSetting(null) represents an explicit blank; omission must be genuinely absent.
                if (setting.Key != "Services:IAM:BaseUrl" || setting.Value is not null)
                    builder.UseSetting(setting.Key, setting.Value);
            }
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                // Only transport substitution: no IAM client/HttpClient registration, principal or authorization override.
                services.Configure<HttpClientFactoryOptions>("IAMService", options => options.HttpMessageHandlerBuilderActions.Add(http =>
                {
                    if (boundary.InspectPrimaryOnly)
                    {
                        boundary.PrimaryRedirectsDisabled = http.PrimaryHandler switch
                        {
                            SocketsHttpHandler sockets => !sockets.AllowAutoRedirect,
                            HttpClientHandler handler => !handler.AllowAutoRedirect,
                            _ => false
                        };
                    }
                    else http.PrimaryHandler = new IamTransport(boundary);
                }));
                services.Configure<HttpClientFactoryOptions>(LegacyServiceAccessTokenProvider.HttpClientName,
                    options => options.HttpMessageHandlerBuilderActions.Add(http => http.PrimaryHandler = new LoginTransport(boundary, fixture.Token("service:legacy-quotation"))));
            });
        }
    }

    private sealed class IamTransport(QuotationNormalIamBoundary boundary) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            boundary.IamCalls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://quotation95-iam.invalid/iam/v1/auth/check-permission", request.RequestUri!.AbsoluteUri);
            Assert.Equal(boundary.ExpectedCredential, Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            var workload = new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization!.Parameter);
            Assert.Equal("service:legacy-quotation", workload.Subject);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(4, json.RootElement.EnumerateObject().Count());
            Assert.Equal("service:legacy-accounting", json.RootElement.GetProperty("principalId").GetString());
            Assert.Equal("legacy.quotations.update", json.RootElement.GetProperty("permissionId").GetString());
            Assert.Equal("global", json.RootElement.GetProperty("resourcePath").GetString());
            Assert.True(json.RootElement.GetProperty("bypassCache").GetBoolean());
            boundary.Entered.TrySetResult();
            if (boundary.Mode == "wait") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            if (boundary.Mode == "network") throw new HttpRequestException("Synthetic dependency unavailable.");
            return new(boundary.Mode == "unavailable" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            { Content = JsonContent.Create(new { allowed = boundary.Mode != "denied" }) };
        }
    }

    private sealed class LoginTransport(QuotationNormalIamBoundary boundary, string token) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            boundary.LoginCalls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Issuer + "/auth/v1/service/login", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
            Assert.Equal("legacy-quotation", json.RootElement.GetProperty("clientId").GetString());
            Assert.Equal("synthetic-quotation95-secret", json.RootElement.GetProperty("clientSecret").GetString());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { accessToken = token, expiresIn = 300 }) };
        }
    }
}

/// <summary>Per-host external boundary observation; never production grant evidence.</summary>
public sealed class QuotationNormalIamBoundary
{
    public string Mode { get; init; } = "allowed";
    public bool InspectPrimaryOnly { get; init; }
    public bool PrimaryRedirectsDisabled { get; set; }
    public string ExpectedCredential { get; set; } = "";
    public int IamCalls;
    public int LoginCalls;
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>External workload-login preparation only; does not register IAM or create authorization grants.</summary>
internal static class QuotationTestWorkloadExchange
{
    private const string Origin = "https://quotation-workload-fixture.invalid";

    internal static void Prepare(IWebHostBuilder builder)
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        using var key = RSA.Create(2048);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Origin, "quotation-workload-fixture",
            [new Claim("sub", "service:legacy-quotation"), new Claim("identity_kind", "service")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));
        builder.UseSetting("Services:Auth:BaseUrl", Origin);
        builder.UseSetting("ServiceAuthentication:ClientId", "legacy-quotation");
        builder.UseSetting("ServiceAuthentication:ClientSecret", secret);
        builder.ConfigureTestServices(services => services.Configure<HttpClientFactoryOptions>(
            LegacyServiceAccessTokenProvider.HttpClientName, options => options.HttpMessageHandlerBuilderActions.Add(
                http => http.PrimaryHandler = new RecordingLoginTransport(secret, token))));
    }

    internal static void AssertQuotationSubject(HttpRequestMessage request)
    {
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        var token = new JwtSecurityTokenHandler().ReadJwtToken(request.Headers.Authorization!.Parameter);
        Assert.Equal(SecurityAlgorithms.RsaSha256, token.Header.Alg);
        Assert.Equal("service:legacy-quotation", token.Subject);
    }

    private sealed class RecordingLoginTransport(string secret, string token) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Origin + "/auth/v1/service/login", request.RequestUri!.AbsoluteUri);
            Assert.Null(request.Headers.Authorization);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal(2, json.RootElement.EnumerateObject().Count());
            Assert.Equal("legacy-quotation", json.RootElement.GetProperty("clientId").GetString());
            Assert.Equal(secret, json.RootElement.GetProperty("clientSecret").GetString());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { accessToken = token, expiresIn = 300 }) };
        }
    }
}
