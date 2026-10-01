using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Data;
using Maliev.Aspire.ServiceDefaults.IAM;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Normal Production HTTP, configured retry strategy and actual IAM client; only remote IAM transport is controlled.</summary>
public sealed class QuotationRequestCreateRetryHttpTests(RequestCreateRetryFixture fixture) : IClassFixture<RequestCreateRetryFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_preserves_server_owned_attribution_with_normal_configured_retry(bool keyed)
    {
        var journey = Guid.NewGuid();
        var transport = new RequestCreateIamTransport(true);
        var diagnostic = new RequestCreateStrategyDiagnostic();
        await using var app = fixture.App(transport, diagnostic);
        using var client = app.CreateClient();
        using (var scope = app.Services.CreateScope())
            Assert.True(scope.ServiceProvider.GetRequiredService<QuotationRequestDbContext>().Database.CreateExecutionStrategy().RetriesOnFailure);
        using var response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? Guid.NewGuid().ToString("D") : null);
        Assert.Equal(1, transport.Calls);
        await using var db = fixture.Context();
        var rows = await db.Requests.Where(x => x.JourneyId == journey).ToListAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"Expected 201; actual={(int)response.StatusCode}; strategyRejected={diagnostic.Rejected}; persistedJourneyRows={rows.Count}.");
        var row = Assert.Single(rows);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = json.RootElement;
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(row.Id, body.GetProperty("Id").GetInt32());
        Assert.Equal("Synthetic", body.GetProperty("FirstName").GetString());
        Assert.Equal("Fixture", body.GetProperty("LastName").GetString());
        Assert.Equal("fixture@example.invalid", body.GetProperty("Email").GetString());
        Assert.Equal("0000000000", body.GetProperty("TelephoneNumber").GetString());
        Assert.Equal("Thailand", body.GetProperty("Country").GetString());
        Assert.Equal("Fixture", body.GetProperty("CompanyName").GetString());
        Assert.Equal("Synthetic request", body.GetProperty("Message").GetString());
        Assert.False(body.GetProperty("Done").GetBoolean());
        Assert.False(body.TryGetProperty("TaxIdentification", out _));
        Assert.False(body.TryGetProperty("InternalComment", out _));
        Assert.NotEqual(default, body.GetProperty("CreatedDate").GetDateTime());
        Assert.Equal(row.CreatedDate, body.GetProperty("CreatedDate").GetDateTime());
        Assert.Equal(row.ModifiedDate, body.GetProperty("ModifiedDate").GetDateTime());
        Assert.Equal(journey, body.GetProperty("JourneyId").GetGuid());
        Assert.Equal($"request-{row.Id}", body.GetProperty("TransactionId").GetString());
        Assert.Equal(row.TransactionId, body.GetProperty("TransactionId").GetString());
        Assert.False(body.TryGetProperty("id", out _));
        Assert.EndsWith($"/quotationrequests/{row.Id}", response.Headers.Location!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(keyed ? 1 : 0, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
    }

    [Theory]
    [InlineData("anonymous", 401)]
    [InlineData("expired", 401)]
    [InlineData("live-denied", 403)]
    [InlineData("no-live-client", 403)]
    public async Task Ordinary_auth_and_live_permission_denials_write_nothing(string scenario, int expected)
    {
        var journey = Guid.NewGuid();
        var transport = new RequestCreateIamTransport(scenario != "live-denied");
        var diagnostic = new RequestCreateStrategyDiagnostic();
        await using var app = fixture.App(scenario == "no-live-client" ? null : transport, diagnostic);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, Payload(journey), scenario == "anonymous" ? null : fixture.Token(scenario == "expired"), Guid.NewGuid().ToString("D"));
        Assert.Equal(expected, (int)response.StatusCode);
        Assert.Equal(scenario == "live-denied" ? 1 : 0, transport.Calls);
        Assert.False(diagnostic.Rejected);
        await using var db = fixture.Context();
        Assert.False(await db.Requests.AnyAsync(x => x.JourneyId == journey));
    }

    [Fact]
    public async Task Overlong_key_stops_before_repository()
    {
        var journey = Guid.NewGuid();
        var transport = new RequestCreateIamTransport(true);
        var diagnostic = new RequestCreateStrategyDiagnostic();
        await using var app = fixture.App(transport, diagnostic);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, Payload(journey), fixture.Token(), new string('k', 129));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("idempotency_key_too_long", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(1, transport.Calls);
        Assert.False(diagnostic.Rejected);
        await using var db = fixture.Context();
        Assert.False(await db.Requests.AnyAsync(x => x.JourneyId == journey));
    }

    [Fact]
    public async Task Malformed_json_stops_before_repository()
    {
        var transport = new RequestCreateIamTransport(true);
        var diagnostic = new RequestCreateStrategyDiagnostic();
        await using var app = fixture.App(transport, diagnostic);
        using var client = app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/quotationrequests")
        {
            Content = new StringContent("{", Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fixture.Token());
        await using var before = fixture.Context();
        var count = await before.Requests.CountAsync();
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, transport.Calls);
        Assert.False(diagnostic.Rejected);
        await using var after = fixture.Context();
        Assert.Equal(count, await after.Requests.CountAsync());
    }

    [Fact]
    public async Task Keyed_replay_and_changed_payload_preserve_one_existing_receipt()
    {
        var journey = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic());
        using var client = app.CreateClient();
        using var first = await SendAsync(client, Payload(journey), fixture.Token(), key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var original = await first.Content.ReadAsStringAsync();
        using var replay = await SendAsync(client, Payload(journey), fixture.Token(), key);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        using var conflict = await SendAsync(client, Payload(journey) with { Message = "Changed synthetic payload" }, fixture.Token(), key);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        Assert.Equal("idempotency_key_conflict", problem.RootElement.GetProperty("code").GetString());
        await using var db = fixture.Context();
        var row = Assert.Single(await db.Requests.Where(x => x.JourneyId == journey).ToListAsync());
        Assert.Equal(1, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
        Assert.Equal("Synthetic request", row.Message);
    }

    [Fact]
    public async Task Concurrent_same_key_returns_same_root_without_duplicate_rows()
    {
        var journey = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic());
        using var client = app.CreateClient();
        var results = await Task.WhenAll(SendAsync(client, Payload(journey), fixture.Token(), key), SendAsync(client, Payload(journey), fixture.Token(), key));
        using var first = results[0];
        using var second = results[1];
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        await using var db = fixture.Context();
        var row = Assert.Single(await db.Requests.Where(x => x.JourneyId == journey).ToListAsync());
        Assert.Equal(1, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proven_precommit_rollback_retries_without_partial_attribution(bool keyed)
    {
        var journey = Guid.NewGuid();
        var fault = new RequestCreatePrecommitFault();
        var rollback = new RequestCreateRollbackObserver();
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), fault, rollback);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? Guid.NewGuid().ToString("D") : null);
        Assert.True(fault.Fired, $"Precommit injection was not reached; actual HTTP={(int)response.StatusCode}.");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, rollback.Rollbacks);
        Assert.Equal(2, fault.Contexts.Count);
        await using var db = fixture.Context();
        var row = Assert.Single(await db.Requests.Where(x => x.JourneyId == journey).ToListAsync());
        Assert.Equal($"request-{row.Id}", row.TransactionId);
        Assert.Equal(keyed ? 1 : 0, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lost_commit_ack_uses_keyed_receipt_but_never_retries_unkeyed(bool keyed)
    {
        var journey = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        var fault = new RequestCreateCommitFault();
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), fault);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? key : null);
        Assert.True(fault.Fired, $"Lost-ack injection was not reached; actual HTTP={(int)response.StatusCode}.");
        await using var db = fixture.Context();
        var rows = await db.Requests.Where(x => x.JourneyId == journey).ToListAsync();
        var ids = rows.Select(x => x.Id).ToArray();
        var receipts = await db.RequestCreateIdempotency.CountAsync(x => ids.Contains(x.RequestId));
        Assert.True(response.StatusCode == (keyed ? HttpStatusCode.Created : HttpStatusCode.ServiceUnavailable),
            $"Lost ACK HTTP={(int)response.StatusCode}; fresh roots={rows.Count}; receipts={receipts}; commitAcknowledgements={fault.CommitAcknowledgements}.");
        var row = Assert.Single(rows);
        Assert.Equal($"request-{row.Id}", row.TransactionId);
        Assert.Equal(1, fault.CommitAcknowledgements);
        Assert.Equal(keyed ? 1 : 0, receipts);
        if (keyed)
        {
            using var replay = await SendAsync(client, Payload(journey), fixture.Token(), key);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var json = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(row.Id, json.RootElement.GetProperty("Id").GetInt32());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Caller_cancellation_preserves_precommit_rollback_or_postcommit_receipt(bool keyed, bool afterCommit)
    {
        var journey = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        using var cancellation = new CancellationTokenSource();
        var pre = new RequestCreatePrecommitFault(cancellation);
        var post = new RequestCreateCommitFault(cancellation);
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), afterCommit ? post : pre);
        using var client = app.CreateClient();
        HttpStatusCode? returned = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? key : null, cancellation.Token);
            returned = response.StatusCode;
        });
        Assert.True(afterCommit ? post.Fired : pre.Fired, $"Cancellation injection was not reached; actual HTTP={(int?)returned}.");
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        await using var db = fixture.Context();
        var rows = await db.Requests.Where(x => x.JourneyId == journey).ToListAsync();
        Assert.Equal(afterCommit ? 1 : 0, rows.Count);
        if (!afterCommit) return;
        Assert.Equal(1, post.CommitAcknowledgements);
        var row = Assert.Single(rows);
        Assert.Equal($"request-{row.Id}", row.TransactionId);
        Assert.Equal(keyed ? 1 : 0, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
        if (keyed)
        {
            using var replay = await SendAsync(client, Payload(journey), fixture.Token(), key);
            Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
            using var json = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            Assert.Equal(row.Id, json.RootElement.GetProperty("Id").GetInt32());
        }
    }

    [Fact]
    public async Task Replay_with_damaged_server_transaction_cannot_fabricate_created_response()
    {
        var journey = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic());
        using var client = app.CreateClient();
        using var first = await SendAsync(client, Payload(journey), fixture.Token(), key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        await using var db = fixture.Context();
        var row = await db.Requests.SingleAsync(x => x.JourneyId == journey);
        row.TransactionId = $"damaged-{journey:D}";
        await db.SaveChangesAsync();
        using var replay = await SendAsync(client, Payload(journey), fixture.Token(), key);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, replay.StatusCode);
        Assert.Equal(1, await db.Requests.CountAsync(x => x.JourneyId == journey));
        Assert.Equal(1, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
    }

    [Theory]
    [InlineData("missing-receipt")]
    [InlineData("changed-fingerprint")]
    [InlineData("missing-root")]
    [InlineData("changed-root")]
    [InlineData("damaged-transaction")]
    public async Task Lost_ack_requires_exact_durable_receipt_root_and_server_attribution(string scenario)
    {
        var journey = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var fault = new RequestCreateCommitFault(mutate: async () =>
        {
            await using var db = fixture.Context();
            var row = await db.Requests.SingleAsync(x => x.JourneyId == journey);
            var receipt = await db.RequestCreateIdempotency.SingleAsync(x => x.KeyHash == hash);
            if (scenario == "missing-receipt") db.RequestCreateIdempotency.Remove(receipt);
            if (scenario == "changed-fingerprint") receipt.Fingerprint = new string('0', 64);
            if (scenario == "missing-root") db.Requests.Remove(row);
            if (scenario == "damaged-transaction") row.TransactionId = $"damaged-{journey:D}";
            if (scenario == "changed-root") receipt.RequestId = row.Id + 1000000;
            await db.SaveChangesAsync();
        });
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), fault);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, Payload(journey), fixture.Token(), key);
        Assert.True(fault.Fired);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, fault.CommitAcknowledgements);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("idempotency_store_unavailable", problem.RootElement.GetProperty("code").GetString());
        await using var verification = fixture.Context();
        Assert.Equal(scenario == "missing-root" ? 0 : 1, await verification.Requests.CountAsync(x => x.JourneyId == journey));
        Assert.Equal(scenario == "missing-receipt" ? 0 : 1, await verification.RequestCreateIdempotency.CountAsync(x => x.KeyHash == hash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unretryable_save_failure_rolls_back_both_saves_without_receipt(bool keyed)
    {
        var journey = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        var fault = new RequestCreateUnretryableSaveFault();
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), fault);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? key : null);
        Assert.True(fault.Fired);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(2, fault.Saves);
        await using var db = fixture.Context();
        Assert.False(await db.Requests.AnyAsync(x => x.JourneyId == journey));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        Assert.False(await db.RequestCreateIdempotency.AnyAsync(x => x.KeyHash == hash));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Postcommit_context_disposal_cannot_trigger_second_create(bool keyed)
    {
        var journey = Guid.NewGuid();
        var fault = new RequestCreateDisposalFault();
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), fault.Commit, fault.Disposal);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? Guid.NewGuid().ToString("D") : null);
        Assert.True(fault.Fired);
        await using var db = fixture.Context();
        var rows = await db.Requests.Where(x => x.JourneyId == journey).ToListAsync();
        Assert.True(response.StatusCode == (keyed ? HttpStatusCode.Created : HttpStatusCode.ServiceUnavailable),
            $"Disposal HTTP={(int)response.StatusCode}; fresh roots={rows.Count}; commits={fault.Commits}.");
        var row = Assert.Single(rows);
        Assert.Equal($"request-{row.Id}", row.TransactionId);
        Assert.Equal(1, fault.Commits);
        Assert.Equal(keyed ? 1 : 0, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
        if (!keyed)
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("request_create_unavailable", json.RootElement.GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Any_postcommit_exception_requires_durable_keyed_proof_or_unkeyed_unavailable(bool keyed)
    {
        var journey = Guid.NewGuid();
        var fault = new RequestCreateArbitraryCommitFault();
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), fault);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? Guid.NewGuid().ToString("D") : null);
        Assert.True(fault.Fired);
        Assert.Equal(1, fault.Commits);
        await using var db = fixture.Context();
        var rows = await db.Requests.Where(x => x.JourneyId == journey).ToListAsync();
        Assert.True(response.StatusCode == (keyed ? HttpStatusCode.Created : HttpStatusCode.ServiceUnavailable),
            $"Arbitrary post-COMMIT HTTP={(int)response.StatusCode}; fresh roots={rows.Count}; commits={fault.Commits}.");
        var row = Assert.Single(rows);
        Assert.Equal(keyed ? 1 : 0, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precommit_invalid_operation_retains_ordinary400_and_rolls_back_without_retry(bool keyed)
    {
        var journey = Guid.NewGuid();
        var fault = new RequestCreateUnretryableSaveFault(true);
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), fault);
        using var client = app.CreateClient();
        using var response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? Guid.NewGuid().ToString("D") : null);
        Assert.True(fault.Fired);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(2, fault.Saves);
        await using var db = fixture.Context();
        Assert.False(await db.Requests.AnyAsync(x => x.JourneyId == journey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unconfirmed_rollback_never_replays_transient_create(bool keyed)
    {
        var journey = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        var save = new RequestCreatePrecommitFault();
        var rollback = new RequestCreateRollbackObserver(true);
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), save, rollback);
        using var client = app.CreateClient();
        var originalObserved = 0;
        var rollbackObserved = 0;
        EventHandler<FirstChanceExceptionEventArgs> observe = (_, args) =>
        {
            if (args.Exception.GetType().Name != "RequestCreateAttemptUnconfirmedException") return;
            if (ContainsReference(args.Exception.InnerException, save.Failure)) Interlocked.Exchange(ref originalObserved, 1);
            if (ContainsReference(args.Exception.InnerException, rollback.Failure)) Interlocked.Exchange(ref rollbackObserved, 1);
        };
        HttpResponseMessage response;
        AppDomain.CurrentDomain.FirstChanceException += observe;
        try { response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? key : null); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
        using var ownedResponse = response;
        Assert.True(save.Fired);
        Assert.True(rollback.Fired);
        Assert.Equal(1, rollback.Rollbacks);
        Assert.Single(save.Contexts);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var db = fixture.Context();
        Assert.False(await db.Requests.AnyAsync(x => x.JourneyId == journey));
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        Assert.False(await db.RequestCreateIdempotency.AnyAsync(x => x.KeyHash == hash));
        Assert.Equal(1, originalObserved);
        Assert.Equal(1, rollbackObserved);
    }

    private static bool ContainsReference(Exception? value, Exception? expected, int depth = 0)
    {
        if (value is null || expected is null || depth >= 8) return false;
        if (ReferenceEquals(value, expected)) return true;
        if (value is AggregateException aggregate)
            return aggregate.InnerExceptions.Take(8).Any(inner => ContainsReference(inner, expected, depth + 1));
        return ContainsReference(value.InnerException, expected, depth + 1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Confirmed_rollback_with_escaping_teardown_retains_both_causes_and_never_replays(bool keyed, bool transient)
    {
        var journey = Guid.NewGuid();
        var key = Guid.NewGuid().ToString("D");
        var transientSave = new RequestCreatePrecommitFault();
        var unretryableSave = new RequestCreateUnretryableSaveFault();
        var rollback = new RequestCreateRollbackObserver();
        var teardown = new RequestCreateRollbackTeardownFault(rollback);
        IInterceptor save = transient ? transientSave : unretryableSave;
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), save, rollback, teardown);
        using var client = app.CreateClient();
        var originalObserved = 0;
        var teardownObserved = 0;
        EventHandler<FirstChanceExceptionEventArgs> observe = (_, args) =>
        {
            if (args.Exception.GetType().Name != "RequestCreateAttemptUnconfirmedException") return;
            var original = transient ? transientSave.Failure : unretryableSave.Failure;
            if (ContainsReference(args.Exception.InnerException, original)) Interlocked.Exchange(ref originalObserved, 1);
            if (ContainsReference(args.Exception.InnerException, teardown.Failure)) Interlocked.Exchange(ref teardownObserved, 1);
        };
        HttpResponseMessage response;
        AppDomain.CurrentDomain.FirstChanceException += observe;
        try { response = await SendAsync(client, Payload(journey), fixture.Token(), keyed ? key : null); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
        using var ownedResponse = response;
        Assert.True(transient ? transientSave.Fired : unretryableSave.Fired);
        Assert.True(teardown.Fired);
        Assert.Equal(1, rollback.Rollbacks);
        Assert.Single(transient ? transientSave.Contexts : unretryableSave.Contexts);
        await using var db = fixture.Context();
        var roots = await db.Requests.CountAsync(x => x.JourneyId == journey);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var receipts = await db.RequestCreateIdempotency.CountAsync(x => x.KeyHash == hash);
        Assert.Equal(0, roots);
        Assert.Equal(0, receipts);
        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Confirmed rollback+teardown HTTP={(int)response.StatusCode}; roots={roots}; receipts={receipts}; originalObserved={originalObserved}; teardownObserved={teardownObserved}.");
        Assert.Equal(1, originalObserved);
        Assert.Equal(1, teardownObserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Keyed_receipt_readback_is_bounded_and_propagates_caller_cancellation(bool callerCancel)
    {
        var journey = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        var ack = new RequestCreateCommitFault();
        var read = new RequestCreateReadbackFault(ack, callerCancel ? cancellation : null);
        await using var app = fixture.App(new RequestCreateIamTransport(true), new RequestCreateStrategyDiagnostic(), ack, read);
        using var client = app.CreateClient();
        HttpStatusCode? returned = null;
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var response = await SendAsync(client, Payload(journey), fixture.Token(), Guid.NewGuid().ToString("D"), cancellation.Token);
            returned = response.StatusCode;
        });
        Assert.True(ack.Fired);
        Assert.True(read.Fired);
        Assert.True(read.Cancelled);
        if (callerCancel) Assert.IsAssignableFrom<OperationCanceledException>(exception);
        else { Assert.Null(exception); Assert.Equal(HttpStatusCode.ServiceUnavailable, returned); }
        Assert.Equal(1, ack.CommitAcknowledgements);
        await using var db = fixture.Context();
        var row = Assert.Single(await db.Requests.Where(x => x.JourneyId == journey).ToListAsync());
        Assert.Equal(1, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
    }

    private static UpsertQuotationRequestRequest Payload(Guid journey) => new("Synthetic", "Fixture", "fixture@example.invalid", "0000000000", "Thailand", "Fixture", null, "Synthetic request", null, false, journey);

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, UpsertQuotationRequestRequest body, string? token, string? key, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/quotationrequests") { Content = JsonContent.Create(body) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return client.SendAsync(request, cancellationToken);
    }
}

/// <summary>Only a safe error category is retained, never log text, tokens, options or request contents.</summary>
public sealed class RequestCreateStrategyDiagnostic : ILoggerProvider
{
    private int rejected;
    public bool Rejected => Volatile.Read(ref rejected) != 0;
    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(this);
    public void Dispose() { }
    private sealed class DiagnosticLogger(RequestCreateStrategyDiagnostic owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            for (var depth = 0; exception is not null && depth < 8; depth++, exception = exception.InnerException)
                if (exception is InvalidOperationException && exception.Message.Contains("user-initiated transactions", StringComparison.Ordinal))
                    Interlocked.Exchange(ref owner.rejected, 1);
        }
    }
}

public sealed class RequestCreateIamTransport(bool allowed) : HttpMessageHandler
{
    public int Calls { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        QuotationTestWorkloadExchange.AssertQuotationSubject(request);
        Calls++;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri!.AbsolutePath);
        Assert.Equal("synthetic-live-check-only", Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
        using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
        Assert.Equal("employee-create-fixture", json.RootElement.GetProperty("principalId").GetString());
        Assert.Equal("legacy.quotation-requests.create", json.RootElement.GetProperty("permissionId").GetString());
        Assert.True(json.RootElement.GetProperty("bypassCache").GetBoolean());
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed }) };
    }
}

/// <summary>One actual transaction boundary fault; injection reach is mandatory, not inferred from an HTTP error.</summary>
public sealed class RequestCreatePrecommitFault(CancellationTokenSource? cancel = null) : SaveChangesInterceptor
{
    private int saves;
    public bool Fired { get; private set; }
    public HashSet<Guid> Contexts { get; } = [];
    public Exception? Failure { get; private set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Contexts.Add(eventData.Context!.ContextId.InstanceId);
        if (Interlocked.Increment(ref saves) == 2)
        {
            Fired = true;
            if (cancel is not null) { cancel.Cancel(); throw new OperationCanceledException(cancellationToken); }
            Failure = new PostgresException("Synthetic precommit serialization rollback.", "ERROR", "ERROR", "40001");
            throw Failure;
        }
        return ValueTask.FromResult(result);
    }
}

public sealed class RequestCreateRollbackObserver(bool loseAck = false) : DbTransactionInterceptor
{
    public int Rollbacks { get; private set; }
    public bool Fired { get; private set; }
    public Exception? Failure { get; private set; }
    public override Task TransactionRolledBackAsync(System.Data.Common.DbTransaction transaction,
        TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Rollbacks++;
        if (loseAck)
        {
            Fired = true;
            Failure = new NpgsqlException("Synthetic rollback acknowledgement loss.", new IOException("Synthetic interruption."));
            throw Failure;
        }
        return Task.CompletedTask;
    }
}

public sealed class RequestCreateCommitFault(CancellationTokenSource? cancel = null, Func<Task>? mutate = null) : DbTransactionInterceptor
{
    private int fired;
    private int acknowledgements;
    public bool Fired => Volatile.Read(ref fired) != 0;
    public int CommitAcknowledgements => Volatile.Read(ref acknowledgements);
    public override async Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
        TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref acknowledgements);
        if (Interlocked.CompareExchange(ref fired, 1, 0) == 0)
        {
            if (mutate is not null) await mutate();
            if (cancel is not null) { cancel.Cancel(); throw new OperationCanceledException(cancellationToken); }
            throw new NpgsqlException("Synthetic lost commit acknowledgement.", new IOException("Synthetic interruption."));
        }
    }
}

public sealed class RequestCreateUnretryableSaveFault(bool invalidOperation = false) : SaveChangesInterceptor
{
    public int Saves { get; private set; }
    public bool Fired { get; private set; }
    public HashSet<Guid> Contexts { get; } = [];
    public Exception? Failure { get; private set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Contexts.Add(eventData.Context!.ContextId.InstanceId);
        if (++Saves == 2)
        {
            Fired = true;
            if (invalidOperation) throw new InvalidOperationException("Synthetic precommit business failure.");
            Failure = new PostgresException("Synthetic unretryable failure.", "ERROR", "ERROR", "P0001");
            throw Failure;
        }
        return ValueTask.FromResult(result);
    }
}

public sealed class RequestCreateRollbackTeardownFault(RequestCreateRollbackObserver rollback) : DbConnectionInterceptor
{
    public bool Fired { get; private set; }
    public Exception? Failure { get; private set; }
    public override ValueTask<InterceptionResult> ConnectionDisposingAsync(System.Data.Common.DbConnection connection,
        ConnectionEventData eventData, InterceptionResult result)
    {
        if (rollback.Rollbacks > 0 && !Fired)
        {
            Fired = true;
            Failure = new InvalidOperationException("Synthetic non-provider rollback teardown failure.");
            throw Failure;
        }
        return ValueTask.FromResult(result);
    }
}

public sealed class RequestCreateArbitraryCommitFault : DbTransactionInterceptor
{
    public int Commits { get; private set; }
    public bool Fired { get; private set; }
    public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
        TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        Commits++;
        Fired = true;
        throw new InvalidOperationException("Synthetic non-provider invariant failure.");
    }
}

public sealed class RequestCreateDisposalFault
{
    public int Commits { get; private set; }
    public bool Fired { get; private set; }
    public DbTransactionInterceptor Commit => new Observer(this);
    public DbConnectionInterceptor Disposal => new DisposalObserver(this);
    private sealed class Observer(RequestCreateDisposalFault owner) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction,
            TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        { owner.Commits++; return Task.CompletedTask; }
    }
    private sealed class DisposalObserver(RequestCreateDisposalFault owner) : DbConnectionInterceptor
    {
        public override ValueTask<InterceptionResult> ConnectionDisposingAsync(System.Data.Common.DbConnection connection,
            ConnectionEventData eventData, InterceptionResult result)
        {
            if (owner.Commits > 0 && !owner.Fired)
            {
                owner.Fired = true;
                throw new NpgsqlException("Synthetic committed context teardown interruption.", new IOException("Synthetic interruption."));
            }
            return ValueTask.FromResult(result);
        }
    }
}

public sealed class RequestCreateReadbackFault(RequestCreateCommitFault ack, CancellationTokenSource? cancel) : DbCommandInterceptor
{
    public bool Fired { get; private set; }
    public bool Cancelled { get; private set; }
    public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
        System.Data.Common.DbCommand command, CommandEventData eventData,
        InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
    {
        if (ack.Fired && !Fired && command.CommandText.Contains("RequestCreateIdempotency", StringComparison.Ordinal))
        {
            Fired = true;
            cancel?.Cancel();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
        }
        return result;
    }
}

public sealed class RequestCreateRetryFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly IContainer redis = new ContainerBuilder("redis:7-alpine").WithPortBinding(6379, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build();
    private readonly RSA key = RSA.Create(2048);
    private string RequestConnection => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "request_create_retry" }.ConnectionString;

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), redis.StartAsync());
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE request_create_retry", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var quotation = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        await quotation.Database.MigrateAsync();
        await using var requests = Context();
        await requests.Database.MigrateAsync();
    }

    public QuotationRequestDbContext Context() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(RequestConnection).Options);
    public string Token(bool expired = false) => new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
        issuer: "https://request-create-auth.example", audience: "request-create-services",
        claims: [new Claim("sub", "employee-create-fixture"), new Claim("identity_kind", "employee"), new Claim("permissions", "legacy.quotation-requests.create")],
        notBefore: DateTime.UtcNow.AddMinutes(-10), expires: DateTime.UtcNow.AddMinutes(expired ? -6 : 5),
        signingCredentials: new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256)));

    public WebApplicationFactory<Program> App(RequestCreateIamTransport? transport, RequestCreateStrategyDiagnostic diagnostic, params IInterceptor[] interceptors) => new Factory(new Dictionary<string, string?>
    {
        ["ConnectionStrings:QuotationDbContext"] = postgres.GetConnectionString(),
        ["ConnectionStrings:QuotationRequestDbContext"] = RequestConnection,
        ["ConnectionStrings:redis"] = $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}",
        ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())),
        ["Jwt:Issuer"] = "https://request-create-auth.example",
        ["Jwt:Audience"] = "request-create-services",
        ["IAM:LivePermissionChecks:Credential"] = "synthetic-live-check-only",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        ["Observability:RuntimeMetricsEnabled"] = "false"
    }, transport, diagnostic, interceptors);

    public async Task DisposeAsync()
    {
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), redis.DisposeAsync().AsTask());
        key.Dispose();
    }

    private sealed class Factory(Dictionary<string, string?> settings, RequestCreateIamTransport? transport, RequestCreateStrategyDiagnostic diagnostic, IInterceptor[] interceptors) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            QuotationTestWorkloadExchange.Prepare(builder);
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(diagnostic); });
            builder.ConfigureTestServices(services =>
            {
                // Append fault instrumentation to the real registered options; never replace provider/retry configuration.
                if (interceptors.Length > 0) services.AddDbContext<QuotationRequestDbContext>(options => options.AddInterceptors(interceptors));
                if (transport is null) return;
                services.AddScoped<IIamServiceClient, IamServiceClient>();
                services.AddHttpClient("IAMService", client => client.BaseAddress = new Uri("https://request-create-iam.example"))
                    .ConfigurePrimaryHttpMessageHandler(() => transport);
            });
        }
    }
}
