using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace QualificationAuthorityJoinedTests;

/// <summary>Real Auth process, exact isolated pins, ordinary RS256, real localhost authority and PostgreSQL18.</summary>
public sealed class QualificationAuthorityJoinedHttpTests(JoinedFixture fixture) : IClassFixture<JoinedFixture>
{
    [Fact]
    public async Task NormalLoginRotationReplayAndFamilyRevocation_UseRealAuthority()
    {
        var login = await fixture.LoginAsync();
        var sid = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken).Claims.Single(x => x.Type == "sid").Value;
        using (var persisted = await fixture.SessionAsync(sid))
        {
            Assert.Equal("joined-employee", persisted.RootElement.GetProperty("IdentityId").GetString());
            Assert.Equal("Employee", persisted.RootElement.GetProperty("kind").GetString());
            Assert.False(persisted.RootElement.GetProperty("rotated").GetBoolean());
        }
        var id = await fixture.SeedRequestAsync();
        await using var app = fixture.QuotationApp();
        using var client = app.CreateClient();
        using (var read = await SendAsync(client, id, false, login.AccessToken)) Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using (var update = await SendAsync(client, id, true, login.AccessToken)) Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        using (var replay = await SendAsync(client, id, true, login.AccessToken)) Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using (var db = fixture.RequestContext())
        {
            Assert.Equal("joined-employee", (await db.RequestQualificationAudit.SingleAsync(x => x.RequestId == id)).ChangedBy);
            Assert.Equal(1, (await db.Requests.SingleAsync(x => x.Id == id)).QualificationVersion);
        }
        var rotated = await fixture.RefreshAsync(login.RefreshToken);
        var replacement = new JwtSecurityTokenHandler().ReadJwtToken(rotated.AccessToken).Claims.Single(x => x.Type == "sid").Value;
        Assert.NotEqual(sid, replacement);
        using (var old = await fixture.SessionAsync(sid)) Assert.True(old.RootElement.GetProperty("rotated").GetBoolean());
        using (var oldRead = await SendAsync(client, id, false, login.AccessToken)) Assert.Equal(HttpStatusCode.OK, oldRead.StatusCode);
        await fixture.RevokeAsync(rotated.RefreshToken);
        using (var deniedRead = await SendAsync(client, id, false, login.AccessToken)) Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        using (var deniedReplay = await SendAsync(client, id, true, login.AccessToken)) Assert.Equal(HttpStatusCode.Forbidden, deniedReplay.StatusCode);
        await using var final = fixture.RequestContext();
        Assert.Single(await final.RequestQualificationAudit.Where(x => x.RequestId == id).ToListAsync());
        Assert.Equal(1, (await final.Requests.SingleAsync(x => x.Id == id)).QualificationVersion);
        fixture.AssertNoCredentialOutput(login.AccessToken, rotated.AccessToken);
    }

    [Fact]
    public async Task CommittedIdentityStampChange_DeniesReadAndUpdateWithoutWrites()
    {
        var login = await fixture.LoginAsync();
        var id = await fixture.SeedRequestAsync();
        await using var app = fixture.QuotationApp();
        using var client = app.CreateClient();
        using (var allowed = await SendAsync(client, id, false, login.AccessToken)) Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await fixture.ChangeStampAsync();
        using (var deniedRead = await SendAsync(client, id, false, login.AccessToken)) Assert.Equal(HttpStatusCode.Forbidden, deniedRead.StatusCode);
        using (var deniedUpdate = await SendAsync(client, id, true, login.AccessToken)) Assert.Equal(HttpStatusCode.Forbidden, deniedUpdate.StatusCode);
        await using var db = fixture.RequestContext();
        Assert.Equal(0, (await db.Requests.SingleAsync(x => x.Id == id)).QualificationVersion);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == id).ToListAsync());
        fixture.AssertNoCredentialOutput(login.AccessToken);
    }

    [Fact]
    public async Task ActuallyIssuedTokenAtExpiry_DeniedWithinOrdinaryValidatorSkew()
    {
        var login = await fixture.LoginAsync();
        var expiry = new JwtSecurityTokenHandler().ReadJwtToken(login.AccessToken).ValidTo;
        var id = await fixture.SeedRequestAsync();
        await using var app = fixture.QuotationApp();
        using var client = app.CreateClient();
        using (var first = await SendAsync(client, id, false, login.AccessToken)) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        // Real issuer token and real wall clock, no forged expiry/sid or identity/session double.
        var delay = expiry - DateTime.UtcNow + TimeSpan.FromMilliseconds(250);
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        using var denied = await SendAsync(client, id, true, login.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        await using var db = fixture.RequestContext();
        Assert.Equal(0, (await db.Requests.SingleAsync(x => x.Id == id)).QualificationVersion);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == id).ToListAsync());
        fixture.AssertNoCredentialOutput(login.AccessToken);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, int id, bool update, string token)
    {
        var request = new HttpRequestMessage(update ? HttpMethod.Put : HttpMethod.Get,
            $"/quotationrequests/{id}/{(update ? "qualification" : "qualification-receipt")}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (update) request.Content = JsonContent.Create(new QualificationStateUpdateRequest("qualified", null, "complete", 0, null, "joined-key", 0));
        return client.SendAsync(request);
    }
}

public sealed record IssuedTokens(string AccessToken, string RefreshToken);

/// <summary>Owns only disposable stores and the exact private Auth child process.</summary>
public sealed class JoinedFixture : IAsyncLifetime
{
    private const string ProducerRevision = "8cdb634b3b0abdf18b9b826a0948dbfd98c66ea0";
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:7-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly RSA key = RSA.Create(2048);
    private readonly StringBuilder output = new();
    private readonly object outputLock = new();
    private Process? auth;
    private HttpClient authHttp = null!;
    private const string Secret = "joined-fixture-only-secret";
    private string Root { get; } = FindRoot();
    private string AuthSource => Path.Combine(Root, "TestResults", ".joined-auth", "Legacy.Maliev.AuthService");
    private string AuthDll => Path.Combine(AuthSource, "Legacy.Maliev.AuthService.Api", "bin", "Release", "net10.0", "Legacy.Maliev.AuthService.Api.dll");
    private string ToolDll => Path.Combine(Root, "tools", "QualificationAuthorityAuthFixture", "bin", "Release", "net10.0", "QualificationAuthorityAuthFixture.dll");
    private string Origin = string.Empty;
    private string Connection(string database) => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "joined_" + database }.ConnectionString;

    public async Task InitializeAsync()
    {
        await VerifyIsolationAsync();
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync());
        await RunToolAsync("seed");
        await using (var quotations = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(Connection("quotations")).Options)) await quotations.Database.MigrateAsync();
        await using (var requests = RequestContext()) await requests.Database.MigrateAsync();
        var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        Origin = $"http://127.0.0.1:{port}";
        var start = StartInfo(AuthDll);
        start.WorkingDirectory = Path.GetDirectoryName(AuthDll)!;
        start.Environment["ASPNETCORE_URLS"] = Origin;
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment.Remove("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES");
        start.Environment["Jwt__Issuer"] = "https://joined-auth.example";
        start.Environment["Jwt__Audience"] = "joined-services";
        start.Environment["Jwt__PrivateKeyPem"] = key.ExportPkcs8PrivateKeyPem();
        start.Environment["Jwt__KeyId"] = "joined-key";
        start.Environment["Jwt__AccessTokenLifetimeSeconds"] = "300";
        start.Environment["QualificationIntrospection__Enabled"] = "true";
        start.Environment["ServiceClients__Clients__legacy-quotation__SecretSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Secret)));
        start.Environment["ServiceClients__Clients__legacy-quotation__Permissions__0"] = "legacy-auth.quotation-qualification.introspect";
        start.Environment["CORS__AllowedOrigins__0"] = "https://localhost";
        start.Environment["OTEL_EXPORTER_OTLP_ENDPOINT"] = "";
        start.Environment["Observability__RuntimeMetricsEnabled"] = "false";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        auth = Process.Start(start) ?? throw new InvalidOperationException("Owned Auth process failed to start.");
        auth.OutputDataReceived += (_, args) => Capture(args.Data);
        auth.ErrorDataReceived += (_, args) => Capture(args.Data);
        auth.BeginOutputReadLine(); auth.BeginErrorReadLine();
        authHttp = new HttpClient { BaseAddress = new Uri(Origin), Timeout = TimeSpan.FromSeconds(10) };
        using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (auth.HasExited) throw new InvalidOperationException("Owned Auth process exited before readiness; output redacted.");
            try
            {
                using var response = await authHttp.GetAsync("/auth/liveness", ready.Token);
                if (response.IsSuccessStatusCode) break;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, ready.Token);
        }
        // Targeted admission, not whole-service readiness: unrelated recovery remains disabled.
        _ = await LoginAsync();
        using (var serviceLogin = await authHttp.PostAsJsonAsync("/auth/v1/service/login", new { clientId = "legacy-quotation", clientSecret = Secret }, ready.Token))
            Assert.Equal(HttpStatusCode.OK, serviceLogin.StatusCode);
        using var readiness = await authHttp.GetAsync("/auth/readiness", ready.Token);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        using var health = JsonDocument.Parse(await readiness.Content.ReadAsStringAsync(ready.Token));
        Assert.Equal("Unhealthy", health.RootElement.GetProperty("checks").GetProperty("auth_employee_recovery_schema").GetProperty("status").GetString());
    }

    public QuotationRequestDbContext RequestContext() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(Connection("requests")).Options);
    public async Task<int> SeedRequestAsync()
    {
        await using var db = RequestContext();
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        var request = new QuotationRequest { QualificationState = "unreviewed", QualificationVersion = 0, CreatedDate = now, ModifiedDate = now };
        db.Requests.Add(request); await db.SaveChangesAsync(); return request.Id;
    }

    public WebApplicationFactory<global::Program> QuotationApp() => new QuotationFactory(new Dictionary<string, string?>
    {
        ["ConnectionStrings:QuotationDbContext"] = Connection("quotations"),
        ["ConnectionStrings:QuotationRequestDbContext"] = Connection("requests"),
        ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}",
        ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())),
        ["Jwt:Issuer"] = "https://joined-auth.example",
        ["Jwt:Audience"] = "joined-services",
        ["QualificationAuthority:Enabled"] = "true",
        ["Services:Auth:BaseUrl"] = Origin,
        ["ServiceAuthentication:ClientId"] = "legacy-quotation",
        ["ServiceAuthentication:ClientSecret"] = Secret,
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        ["Observability:RuntimeMetricsEnabled"] = "false"
    });

    public async Task<IssuedTokens> LoginAsync()
    {
        using var response = await authHttp.PostAsJsonAsync("/auth/v1/login", new { userName = "joined@example.test", password = "joined-password", identityKind = 1 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IssuedTokens>())!;
    }
    public async Task<IssuedTokens> RefreshAsync(string token)
    {
        using var response = await authHttp.PostAsJsonAsync("/auth/v1/refresh", new { refreshToken = token });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<IssuedTokens>())!;
    }
    public async Task RevokeAsync(string token)
    {
        using var response = await authHttp.PostAsJsonAsync("/auth/v1/revoke", new { refreshToken = token });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }
    public Task<string> ChangeStampAsync() => RunToolAsync("stamp");
    public async Task<JsonDocument> SessionAsync(string sid) => JsonDocument.Parse(await RunToolAsync("session", sid));

    private ProcessStartInfo StartInfo(string dll)
    {
        if (!File.Exists(dll)) throw new InvalidOperationException("Build the approved isolated fixture artifacts first.");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(dll);
        start.Environment["ConnectionStrings__EmployeeIdentity"] = Connection("employees");
        start.Environment["ConnectionStrings__CustomerIdentity"] = Connection("customers");
        start.Environment["ConnectionStrings__RefreshSessions"] = Connection("sessions");
        return start;
    }
    private async Task<string> RunToolAsync(string operation, string? sid = null)
    {
        var start = StartInfo(ToolDll); start.ArgumentList.Add(operation);
        if (sid is not null) start.Environment["JOINED_SESSION_ID"] = sid;
        using var child = Process.Start(start)!;
        var stdout = ReadBoundedAsync(child.StandardOutput);
        var stderr = ReadBoundedAsync(child.StandardError);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(deadline.Token); }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
        Assert.True(child.ExitCode == 0, "Owned EF fixture operation failed; details redacted.");
        var text = await stdout; _ = await stderr;
        Assert.True(text.Length < 4096, "Fixture output exceeded its bounded contract.");
        return text;
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var captured = new StringBuilder();
        var buffer = new char[512];
        var exceeded = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            var remaining = 4096 - captured.Length;
            captured.Append(buffer, 0, Math.Min(remaining, count));
            exceeded |= count > remaining;
        }
        if (exceeded) throw new InvalidOperationException("Owned fixture output exceeded its bounded contract; details redacted.");
        return captured.ToString();
    }
    private async Task VerifyIsolationAsync()
    {
        async Task Pin(string path, string expected)
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-C", path, "rev-parse", "HEAD" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            var text = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); Assert.Equal(expected, text.Trim());
        }
        await Pin(AuthSource, ProducerRevision);
        await Pin(Path.Combine(Root, "TestResults", ".joined-auth", ".dependencies", "Legacy.Maliev.ServiceDefaults"), "5c5f9479313710fa576f83d3b396442997a2fcf4");
        await Pin(Path.Combine(Root, "TestResults", ".bridge-dependencies", "Legacy.Maliev.ServiceDefaults"), "8f4f5f27b226ffe406c4c79b1903742e8c2e7dd3");
        await Pin(Path.Combine(Root, "TestResults", ".joined-auth", ".dependencies", "Legacy.Maliev.CompatibilityContracts"), "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7");
        await Pin(Path.Combine(Root, "TestResults", ".bridge-dependencies", "Legacy.Maliev.CompatibilityContracts"), "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7");
        var authDefaults = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(AuthDll)!, "Legacy.Maliev.ServiceDefaults.dll"));
        var builtAuth = File.ReadAllBytes(Path.Combine(Root, "TestResults", ".joined-auth", ".dependencies", "Legacy.Maliev.ServiceDefaults", "src", "Legacy.Maliev.ServiceDefaults", "bin", "Release", "net10.0", "Legacy.Maliev.ServiceDefaults.dll"));
        Assert.Equal(SHA256.HashData(builtAuth), SHA256.HashData(authDefaults));
        var quotaDefaults = File.ReadAllBytes(typeof(Maliev.Aspire.ServiceDefaults.Authorization.RequirePermissionAttribute).Assembly.Location);
        var ownQuota = File.ReadAllBytes(Path.Combine(Root, "Legacy.Maliev.QuotationService.Api", "bin", "Release", "net10.0", "Legacy.Maliev.ServiceDefaults.dll"));
        Assert.Equal(SHA256.HashData(ownQuota), SHA256.HashData(quotaDefaults));
        Assert.False(SHA256.HashData(authDefaults).SequenceEqual(SHA256.HashData(quotaDefaults)), "Distinct pinned Defaults must not unify.");
    }
    private void Capture(string? text)
    {
        if (text is null) return;
        lock (outputLock) { output.AppendLine(text); if (output.Length > 32768) output.Remove(0, output.Length - 32768); }
    }
    public void AssertNoCredentialOutput(params string[] tokens)
    {
        lock (outputLock)
        {
            Assert.False(output.ToString().Contains(Secret, StringComparison.Ordinal), "Child output exposed a fixture credential.");
            foreach (var token in tokens) Assert.False(output.ToString().Contains(token, StringComparison.Ordinal), "Child output exposed a fixture token.");
        }
    }
    public async Task DisposeAsync()
    {
        if (auth is not null)
        {
            if (!auth.HasExited)
            {
                if (auth.StartInfo.FileName != "dotnet" || !auth.StartInfo.ArgumentList.Contains(AuthDll)) throw new InvalidOperationException("Owned child identity mismatch; refusing cleanup.");
                auth.Kill(true); await auth.WaitForExitAsync();
            }
            auth.Dispose();
        }
        authHttp?.Dispose(); key.Dispose();
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
    }
    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null) { if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.QuotationService.slnx"))) return directory.FullName; directory = directory.Parent; }
        throw new InvalidOperationException("Owned Quotation checkout cannot be located.");
    }
    private sealed class QuotationFactory(Dictionary<string, string?> settings) : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
        }
    }
}
