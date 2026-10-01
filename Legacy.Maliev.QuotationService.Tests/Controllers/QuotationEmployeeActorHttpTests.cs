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
using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Actual Program, production RS256 and permission handlers, disposable PostgreSQL18/Redis.
/// Only external IAM answers are synthetic; these tests do not prove live IAM or Intranet delegation.</summary>
public sealed class QuotationEmployeeActorHttpTests(QuotationEmployeeActorFixture fixture)
    : IClassFixture<QuotationEmployeeActorFixture>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EmployeeIssuerShape_PersistsExactSubjectAndImmutableReceipt()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seeded.Id, Employee("employee-42"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, received {(int)response.StatusCode}: {body}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal("employee-42", json.RootElement.GetProperty("Events")[0].GetProperty("ChangedBy").GetString());
        Assert.False(json.RootElement.GetProperty("Events")[0].TryGetProperty("IdempotencyKey", out _));
        Assert.False(json.RootElement.TryGetProperty("FirstName", out _));
        Assert.DoesNotContain("synthetic-private-email", body, StringComparison.Ordinal);
        await using var db = fixture.RequestContext();
        var stored = await db.Requests.SingleAsync(x => x.Id == seeded.Id);
        var audit = await db.RequestQualificationAudit.SingleAsync(x => x.RequestId == seeded.Id);
        Assert.Equal("employee-42", audit.ChangedBy);
        Assert.Equal("qualified", stored.QualificationState);
        Assert.Equal(1, stored.QualificationVersion);
        Assert.Equal(stored.QualificationStateChangedUtc, audit.ChangedUtc);
        Assert.Equal(seeded.JourneyId, audit.JourneyId);
        Assert.Equal($"request-{seeded.Id}", audit.TransactionId);
    }

    public static TheoryData<string> InvalidActors => new()
    {
        "customer-uri", "workload-uri", "name-only", "uri-only", "missing-sub", "blank-sub",
        "oversized-sub", "duplicate-sub", "duplicate-kind", "missing-kind", "wrong-case-kind",
        "service-sub-employee-kind", "conflicting-user-id", "conflicting-uri", "duplicate-user-id",
        "role-only", "customer-with-employee-role",
    };

    [Theory, MemberData(nameof(InvalidActors))]
    public async Task InvalidActor_FailsClosedWithoutAnyMutation(string profile)
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seeded.Id, Actor(profile));
        // The normal JWT validator itself rejects an array-valued duplicate sub.
        Assert.Equal(profile == "duplicate-sub" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task ConsistentStableAliases_DoNotChangeCanonicalSubject()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seeded.Id,
            [.. Employee("employee-42"), new("user_id", "employee-42"), new(ClaimTypes.NameIdentifier, "employee-42")]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = fixture.RequestContext();
        Assert.Equal("employee-42", (await db.RequestQualificationAudit.SingleAsync(x => x.RequestId == seeded.Id)).ChangedBy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LivePermissionDeniedOrUnavailable_CannotFallBackToTokenGrant(bool unavailable)
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App(allowed: false, unavailable: unavailable);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seeded.Id, Employee("employee-42"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUnchangedAsync(seeded);
    }

    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expired")]
    [InlineData("algorithm")]
    [InlineData("invoice-delegation")]
    public async Task InvalidBearer_IsRejectedBeforeMutation(string failure)
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seeded.Id, Employee("employee-42"), bearerFailure: failure);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task ClientActorHeaderAndBody_CannotOverrideVerifiedEmployee()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        using var request = Request(seeded.Id, Employee("employee-42"));
        request.Headers.Add("X-Actor-Id", "forged-employee");
        request.Content = JsonContent.Create(new { State = "qualified", IdempotencyKey = "review-1", ExpectedVersion = 0, ChangedBy = "forged-employee", Actor = "forged-employee" });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var db = fixture.RequestContext();
        Assert.Equal("employee-42", (await db.RequestQualificationAudit.SingleAsync(x => x.RequestId == seeded.Id)).ChangedBy);
    }

    [Fact]
    public async Task ExistingDurableActorBinding_RejectsCrossEmployeeReplay()
    {
        var seeded = await fixture.SeedAsync(qualified: true);
        await using var app = fixture.App();
        using var client = app.CreateClient();
        // Matching URI is included to expose the existing repository bug before the actor repair.
        using var response = await SendAsync(client, seeded.Id,
            [.. Employee("employee-99"), new(ClaimTypes.NameIdentifier, "employee-99")]);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"Expected 409, received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        await using var db = fixture.RequestContext();
        var audit = await db.RequestQualificationAudit.SingleAsync(x => x.RequestId == seeded.Id);
        Assert.Equal("employee-42", audit.ChangedBy);
        Assert.Equal(seeded.QualificationStateChangedUtc, audit.ChangedUtc);
        Assert.Equal(1, (await db.Requests.SingleAsync(x => x.Id == seeded.Id)).QualificationVersion);
    }

    [Fact]
    public async Task SameEmployeeReplay_PreservesFirstEventAndRejectsChangedPayload()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        using var first = await SendAsync(client, seeded.Id, Employee("employee-42"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstReceipt = await first.Content.ReadAsStringAsync();
        using var retry = await SendAsync(client, seeded.Id, Employee("employee-42"));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(firstReceipt, await retry.Content.ReadAsStringAsync());
        using var mismatch = await SendAsync(client, seeded.Id, Employee("employee-42"),
            command: new("duplicate", "duplicate drawing", null, 1, null, "review-1", 0));
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        await using var db = fixture.RequestContext();
        Assert.Single(await db.RequestQualificationAudit.Where(x => x.RequestId == seeded.Id).ToListAsync());
    }

    [Fact]
    public async Task ConcurrentSameEmployeeSameKey_ReturnsOneOriginalEvent()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        var replies = await Task.WhenAll(SendAsync(client, seeded.Id, Employee("employee-42")),
            SendAsync(client, seeded.Id, Employee("employee-42")));
        try
        {
            Assert.All(replies, reply => Assert.Equal(HttpStatusCode.OK, reply.StatusCode));
            Assert.Equal(await replies[0].Content.ReadAsStringAsync(), await replies[1].Content.ReadAsStringAsync());
            await using var db = fixture.RequestContext();
            Assert.Single(await db.RequestQualificationAudit.Where(x => x.RequestId == seeded.Id).ToListAsync());
            Assert.Equal(1, (await db.Requests.SingleAsync(x => x.Id == seeded.Id)).QualificationVersion);
        }
        finally { foreach (var reply in replies) reply.Dispose(); }
    }

    [Fact]
    public async Task ConcurrentNewKeysSameExpectedVersion_HasOneProjectionWinner()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        var replies = await Task.WhenAll(SendAsync(client, seeded.Id, Employee("employee-42")),
            SendAsync(client, seeded.Id, Employee("employee-99"), command: new("qualified", null, null, 0, null, "review-2", 0)));
        try
        {
            Assert.Equal(1, replies.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, replies.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            await using var db = fixture.RequestContext();
            Assert.Single(await db.RequestQualificationAudit.Where(x => x.RequestId == seeded.Id).ToListAsync());
            Assert.Equal(1, (await db.Requests.SingleAsync(x => x.Id == seeded.Id)).QualificationVersion);
        }
        finally { foreach (var reply in replies) reply.Dispose(); }
    }

    [Theory]
    [InlineData("state")]
    [InlineData("reason")]
    [InlineData("completeness")]
    [InlineData("unmatched")]
    [InlineData("key")]
    [InlineData("duplicates")]
    [InlineData("version")]
    public async Task ConstructorValidationLimits_RejectBadInputBeforePersistence(string field)
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        var command = new QualificationStateUpdateRequest("qualified", null, null, 0, null, "review-1", 0);
        command = field switch
        {
            "state" => command with { State = new string('s', 33) },
            "reason" => command with { Reason = new string('r', 513) },
            "completeness" => command with { Completeness = new string('c', 33) },
            "unmatched" => command with { UnmatchedClassification = new string('u', 65) },
            "key" => command with { IdempotencyKey = new string('k', 129) },
            "duplicates" => command with { DuplicateCount = -1 },
            "version" => command with { ExpectedVersion = -1 },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        using var response = await SendAsync(client, seeded.Id, Employee("employee-42"), command);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task ConcurrentCrossActorSameKey_HasOneImmutableWinner()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        var replies = await Task.WhenAll(SendAsync(client, seeded.Id, Employee("employee-42")),
            SendAsync(client, seeded.Id, Employee("employee-99")));
        try
        {
            Assert.Equal(1, replies.Count(x => x.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, replies.Count(x => x.StatusCode == HttpStatusCode.Conflict));
            await using var db = fixture.RequestContext();
            var audit = await db.RequestQualificationAudit.SingleAsync(x => x.RequestId == seeded.Id);
            var winner = replies.Single(x => x.StatusCode == HttpStatusCode.OK);
            var receipt = await winner.Content.ReadFromJsonAsync<QualificationReceipt>(JsonOptions);
            Assert.Equal(audit.ChangedBy, Assert.Single(receipt!.Events).ChangedBy);
            Assert.Equal(1, (await db.Requests.SingleAsync(x => x.Id == seeded.Id)).QualificationVersion);
        }
        finally { foreach (var reply in replies) reply.Dispose(); }
    }

    [Fact]
    public async Task MissingEntityAndStaleVersion_PreserveExistingStatusContract()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App();
        using var client = app.CreateClient();
        using var missing = await SendAsync(client, int.MaxValue, Employee("employee-42"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var stale = await SendAsync(client, seeded.Id, Employee("employee-42"), command: new("qualified", null, null, 0, null, "review-1", 1));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await AssertUnchangedAsync(seeded);
    }

    [Fact]
    public async Task DatabaseFailure_RollsBackProjectionAndAudit()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App(interceptor: new RejectQualificationSave());
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seeded.Id, Employee("employee-42"));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        await AssertUnchangedAsync(seeded);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderRetry_ReconcilesRolledBackOrCommittedTransition(bool acknowledgementLost)
    {
        var seeded = await fixture.SeedAsync();
        IInterceptor fault = acknowledgementLost ? new LoseCommitAcknowledgement() : new FailFirstSavedQualification();
        await using var app = fixture.App(interceptor: fault);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, seeded.Id,
            [.. Employee("employee-42"), new(ClaimTypes.NameIdentifier, "employee-42")]);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Expected 200, received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.True(fault is FailFirstSavedQualification { Injected: true } or LoseCommitAcknowledgement { Injected: true });
        await using var db = fixture.RequestContext();
        var audit = await db.RequestQualificationAudit.SingleAsync(x => x.RequestId == seeded.Id);
        Assert.Equal("employee-42", audit.ChangedBy);
        Assert.Equal(1, audit.Version);
        Assert.Equal(1, (await db.Requests.SingleAsync(x => x.Id == seeded.Id)).QualificationVersion);
        var receipt = await response.Content.ReadFromJsonAsync<QualificationReceipt>(JsonOptions);
        Assert.Equal(audit.Id, Assert.Single(receipt!.Events).Id);
        Assert.Equal(audit.ChangedUtc, Assert.Single(receipt.Events).ChangedUtc);
    }

    [Fact]
    public async Task PostCommitCacheFailure_ReplayInvalidatesRealRedisWithoutAnotherAudit()
    {
        var seeded = await fixture.SeedAsync();
        await using var app = fixture.App(cacheFailure: true);
        using var client = app.CreateClient();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IQuotationService>();
            var detail = await service.GetRequestAsync(seeded.Id, CancellationToken.None);
            Assert.NotNull(detail);
            var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
            await cache.SetAsync($"request:{seeded.Id}", detail, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.NotNull(await cache.GetAsync<QuotationRequestResponse>($"request:{seeded.Id}", CancellationToken.None));
        }
        using var failed = await SendAsync(client, seeded.Id, Employee("employee-42"));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        await using var db = fixture.RequestContext();
        var original = await db.RequestQualificationAudit.SingleAsync(x => x.RequestId == seeded.Id);
        using var replay = await SendAsync(client, seeded.Id, Employee("employee-42"));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var receipt = await replay.Content.ReadFromJsonAsync<QualificationReceipt>(JsonOptions);
        Assert.Equal(original.Id, Assert.Single(receipt!.Events).Id);
        Assert.Equal(original.ChangedUtc, Assert.Single(receipt.Events).ChangedUtc);
        Assert.Single(await db.RequestQualificationAudit.Where(x => x.RequestId == seeded.Id).ToListAsync());
        await using var readScope = app.Services.CreateAsyncScope();
        var readCache = readScope.ServiceProvider.GetRequiredService<IQuotationCache>();
        Assert.Null(await readCache.GetAsync<QuotationRequestResponse>($"request:{seeded.Id}", CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDuringAtomicSave_PropagatesAndRollsBack()
    {
        var seeded = await fixture.SeedAsync();
        var cancellation = new PauseQualificationSave();
        await using var app = fixture.App(interceptor: cancellation);
        using var client = app.CreateClient();
        using var token = new CancellationTokenSource();
        using var request = Request(seeded.Id, Employee("employee-42"));
        var pending = client.SendAsync(request, token.Token);
        await cancellation.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
        token.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await cancellation.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await AssertUnchangedAsync(seeded);
    }

    private async Task AssertUnchangedAsync(QuotationRequest seeded)
    {
        await using var db = fixture.RequestContext();
        var stored = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.Equal(seeded.QualificationState, stored.QualificationState);
        Assert.Equal(seeded.QualificationVersion, stored.QualificationVersion);
        Assert.Equal(seeded.QualificationStateChangedUtc, stored.QualificationStateChangedUtc);
        Assert.Equal(seeded.ModifiedDate, stored.ModifiedDate);
        Assert.Empty(await db.RequestQualificationAudit.Where(x => x.RequestId == seeded.Id).ToListAsync());
    }

    private static Claim[] Employee(string subject) => [new("sub", subject), new("identity_kind", "employee"), new("permissions", QuotationPermissions.RequestsUpdate)];
    private static Claim[] Actor(string profile) => profile switch
    {
        "customer-uri" => [new("sub", "customer-42"), new("identity_kind", "customer"), new(ClaimTypes.NameIdentifier, "customer-42")],
        "workload-uri" => [new("sub", "service:legacy-intranet"), new("identity_kind", "service"), new(ClaimTypes.NameIdentifier, "service:legacy-intranet")],
        "name-only" => [new("name", "employee@example.test"), new("identity_kind", "employee")],
        "uri-only" => [new(ClaimTypes.NameIdentifier, "employee-42"), new("identity_kind", "employee")],
        "missing-sub" => [new("identity_kind", "employee")],
        "blank-sub" => Employee(" "),
        "oversized-sub" => Employee(new string('a', 257)),
        "duplicate-sub" => [.. Employee("employee-42"), new("sub", "employee-99")],
        "duplicate-kind" => [.. Employee("employee-42"), new("identity_kind", "service")],
        "missing-kind" => [new("sub", "employee-42")],
        "wrong-case-kind" => [new("sub", "employee-42"), new("identity_kind", "Employee")],
        "service-sub-employee-kind" => Employee("service:legacy-intranet"),
        "conflicting-user-id" => [.. Employee("employee-42"), new("user_id", "employee-99")],
        "conflicting-uri" => [.. Employee("employee-42"), new(ClaimTypes.NameIdentifier, "employee-99")],
        "duplicate-user-id" => [.. Employee("employee-42"), new("user_id", "employee-42"), new("user_id", "employee-42")],
        "role-only" => [new("sub", "employee-42"), new("role", "Employee"), new(ClaimTypes.NameIdentifier, "employee-42")],
        "customer-with-employee-role" => [new("sub", "customer-42"), new("identity_kind", "customer"), new("role", "Employee"), new(ClaimTypes.NameIdentifier, "customer-42")],
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, int id, Claim[] claims,
        QualificationStateUpdateRequest? command = null, string? bearerFailure = null)
    {
        using var request = Request(id, claims, command, bearerFailure);
        return await client.SendAsync(request);
    }

    private HttpRequestMessage Request(int id, Claim[] claims, QualificationStateUpdateRequest? command = null, string? failure = null)
    {
        var now = DateTime.UtcNow;
        SecurityKey key = failure switch
        {
            "signature" => new RsaSecurityKey(fixture.WrongKey),
            "algorithm" => new SymmetricSecurityKey(Encoding.UTF8.GetBytes("synthetic-fixture-only-hmac-key-at-least-32-bytes")), // gitleaks:allow
            _ => new RsaSecurityKey(fixture.SigningKey),
        };
        var token = new JwtSecurityToken(failure == "issuer" ? "https://wrong.example" : "https://fixture-iam.example",
            failure == "invoice-delegation" ? "legacy-accounting:invoice-create" : failure == "audience" ? "wrong" : "fixture-quotation",
            claims, now.AddMinutes(-20), failure == "expired" ? now.AddMinutes(-10) : now.AddMinutes(5),
            new SigningCredentials(key, failure == "algorithm" ? SecurityAlgorithms.HmacSha256 : SecurityAlgorithms.RsaSha256));
        var request = new HttpRequestMessage(HttpMethod.Put, $"/quotationrequests/{id}/qualification")
        {
            Content = JsonContent.Create(command ?? new("qualified", null, null, 0, null, "review-1", 0)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return request;
    }

    private sealed class RejectQualificationSave : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RequestQualificationAudit>().Any(x => x.State == EntityState.Added))
                throw new DbUpdateException("Synthetic qualification persistence failure.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailFirstSavedQualification : SaveChangesInterceptor
    {
        private int calls;
        public bool Injected { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Injected = true;
                throw new NpgsqlException("Synthetic retryable failure before commit.", new IOException("Synthetic connection interruption."));
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class LoseCommitAcknowledgement : DbTransactionInterceptor
    {
        private int calls;
        public bool Injected { get; private set; }
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                Injected = true;
                throw new NpgsqlException("Synthetic lost commit acknowledgement.", new IOException("Synthetic connection interruption."));
            }
            return Task.CompletedTask;
        }
    }

    private sealed class PauseQualificationSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(); throw; }
            return result;
        }
    }
}

/// <summary>Fixture-owned disposable stores, keys and actual production application entry point.</summary>
public sealed class QuotationEmployeeActorFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:7-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    public RSA SigningKey { get; } = RSA.Create(2048);
    public RSA WrongKey { get; } = RSA.Create(2048);
    private string RequestConnection => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "quotation70_requests" }.ConnectionString;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync());
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE quotation70_requests", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var quotations = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        await quotations.Database.MigrateAsync();
        await using var requests = RequestContext();
        await requests.Database.MigrateAsync();
    }

    public QuotationRequestDbContext RequestContext() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(RequestConnection).Options);

    public async Task<QuotationRequest> SeedAsync(bool qualified = false)
    {
        await using var db = RequestContext();
        var time = new DateTime(2026, 9, 30, 4, 5, 6);
        var request = new QuotationRequest
        {
            JourneyId = Guid.NewGuid(),
            Email = "synthetic-private-email@example.test",
            CreatedDate = time,
            ModifiedDate = time,
            QualificationState = qualified ? "qualified" : "unreviewed",
            QualificationVersion = qualified ? 1 : 0,
            QualificationStateChangedUtc = qualified ? time : null
        };
        db.Requests.Add(request);
        await db.SaveChangesAsync();
        request.TransactionId = $"request-{request.Id}";
        if (qualified) db.RequestQualificationAudit.Add(new RequestQualificationAudit
        {
            RequestId = request.Id,
            JourneyId = request.JourneyId,
            TransactionId = request.TransactionId,
            IdempotencyKey = "review-1",
            PreviousState = "unreviewed",
            NewState = "qualified",
            ChangedBy = "employee-42",
            ChangedUtc = time,
            Version = 1
        });
        await db.SaveChangesAsync();
        return request;
    }

    public WebApplicationFactory<Program> App(bool allowed = true, bool unavailable = false, IInterceptor? interceptor = null, bool cacheFailure = false) =>
        new ProductionFactory(new Dictionary<string, string?>
        {
            ["ConnectionStrings:QuotationDbContext"] = postgres.GetConnectionString(),
            ["ConnectionStrings:QuotationRequestDbContext"] = RequestConnection,
            ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}",
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(SigningKey.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = "https://fixture-iam.example",
            ["Jwt:Audience"] = "fixture-quotation",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
            ["Observability:RuntimeMetricsEnabled"] = "false",
        }, allowed, unavailable, interceptor, cacheFailure);

    public async Task DisposeAsync()
    {
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
        SigningKey.Dispose(); WrongKey.Dispose();
    }

    private sealed class ProductionFactory(Dictionary<string, string?> settings, bool allowed, bool unavailable, IInterceptor? interceptor, bool cacheFailure)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            foreach (var item in settings) builder.UseSetting(item.Key, item.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                // Keep real policy provider/handler. Only the authoritative remote IAM answer is controlled.
                var iam = new Mock<IIamServiceClient>(MockBehavior.Strict);
                var check = iam.Setup(x => x.CheckPermissionLiveAsync(It.IsAny<string>(), QuotationPermissions.RequestsUpdate, "global", It.IsAny<CancellationToken>()));
                if (unavailable) check.ThrowsAsync(new HttpRequestException("Synthetic IAM unavailable."));
                else check.ReturnsAsync(allowed);
                services.RemoveAll<IIamServiceClient>();
                services.AddSingleton(iam.Object);
                if (interceptor is not null) services.AddDbContext<QuotationRequestDbContext>(options => options.AddInterceptors(interceptor));
                if (cacheFailure)
                {
                    var fault = new EvictionFault();
                    services.AddScoped<IQuotationCache>(provider => new FailFirstEvictionCache(provider.GetRequiredService<DistributedQuotationCache>(), fault));
                }
            });
        }
    }

    private sealed class EvictionFault { public int Calls; }
    private sealed class FailFirstEvictionCache(IQuotationCache inner, EvictionFault fault) : IQuotationCache
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class => inner.GetAsync<T>(key, cancellationToken);
        public Task SetAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken) where T : class => inner.SetAsync(key, value, lifetime, cancellationToken);
        public Task RemoveAsync(string key, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref fault.Calls) == 1) throw new IOException("Synthetic postcommit cache eviction failure.");
            return inner.RemoveAsync(key, cancellationToken);
        }
    }
}
