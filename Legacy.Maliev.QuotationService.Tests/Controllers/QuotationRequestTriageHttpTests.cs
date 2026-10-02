using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.ExceptionServices;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using System.Data.Common;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
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
using StackExchange.Redis;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Normal JWT/Production HTTP lifecycle; real databases, Redis and IAM client with controlled remote IAM HTTP only.</summary>
public sealed class QuotationRequestTriageHttpTests(RequestTriageFixture fixture) : IClassFixture<RequestTriageFixture>
{
    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("\\")]
    public async Task Text_search_treats_source_contains_metacharacters_literally(string character)
    {
        var marker = Guid.NewGuid().ToString("N");
        var literal = await fixture.SeedAsync(marker + character);
        var distractor = await fixture.SeedAsync(marker + "X");
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync("/quotationrequests?search=" + Uri.EscapeDataString(marker + character));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        var ids = Ids(json);
        Assert.Contains(literal.Id, ids);
        Assert.DoesNotContain(distractor.Id, ids);
        Assert.Equal(1, json.RootElement.GetProperty("TotalRecords").GetInt32());
    }

    [Theory]
    [InlineData("RequestId_Ascending", false)]
    [InlineData("RequestId_Descending", true)]
    [InlineData("RequestCreatedDate_Ascending", false)]
    [InlineData("RequestCreatedDate_Descending", true)]
    [InlineData("RequestModifiedDate_Ascending", false)]
    [InlineData("RequestModifiedDate_Descending", true)]
    public async Task List_retains_six_source_sorts_and_attribution(string sort, bool descending)
    {
        var marker = Guid.NewGuid().ToString("N");
        var first = await fixture.SeedAsync(marker, DateTime.UtcNow.AddDays(-2));
        var second = await fixture.SeedAsync(marker, DateTime.UtcNow.AddDays(-1));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotationrequests?search={marker}&sort={sort}&size=1&index=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal([descending ? second.Id : first.Id], Ids(json));
        Assert.Equal(2, json.RootElement.GetProperty("TotalRecords").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("TotalPages").GetInt32());
        Assert.True(json.RootElement.GetProperty("HasNextPage").GetBoolean());
        var row = json.RootElement.GetProperty("Items")[0];
        Assert.Equal(descending ? second.JourneyId : first.JourneyId, row.GetProperty("JourneyId").GetGuid());
        Assert.Equal($"request-{row.GetProperty("Id").GetInt32()}", row.GetProperty("TransactionId").GetString());
    }

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    [InlineData("Message")]
    [InlineData("TelephoneNumber")]
    [InlineData("CompanyName")]
    [InlineData("Email")]
    [InlineData("InternalComment")]
    [InlineData("Country")]
    public async Task Text_search_covers_source_field_case_insensitively(string field)
    {
        var marker = Guid.NewGuid().ToString("N");
        var row = await fixture.SeedAsync("unrelated", field: field, value: marker.ToUpperInvariant());
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync("/quotationrequests?search=" + marker);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal([row.Id], Ids(json));
    }

    [Fact]
    public async Task Numeric_search_and_page_bounds_preserve_current_consumer_contract()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotationrequests?search={row.Id}&index=0&size=0");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal([row.Id], Ids(json));
        Assert.Equal(1, json.RootElement.GetProperty("PageIndex").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("TotalPages").GetInt32());
        using var outOfRange = await client.GetAsync($"/quotationrequests?search={row.Id}&index=2&size=1");
        // Characterization only: source returns404; current Intranet accepts either empty-page form.
        Assert.Equal(HttpStatusCode.OK, outOfRange.StatusCode);
        using var empty = await JsonAsync(outOfRange);
        Assert.Empty(Ids(empty));
        Assert.Equal(1, empty.RootElement.GetProperty("TotalRecords").GetInt32());
        using var missing = await client.GetAsync("/quotationrequests?search=" + Guid.NewGuid().ToString("N"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var missingDetail = await client.GetAsync("/quotationrequests/2147483647");
        Assert.Equal(HttpStatusCode.NotFound, missingDetail.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Ordinary_update_and_done_preserve_attribution_and_immutable_history(bool? done)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var cached = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        using var update = Update(row, done, expected: row.ModifiedDate);
        using var response = await client.SendAsync(update);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        var audit = Assert.Single(await db.RequestQualificationAudit.Where(x => x.RequestId == row.Id).ToListAsync());
        Assert.Equal(row.JourneyId, persisted.JourneyId);
        Assert.Equal(row.TransactionId, persisted.TransactionId);
        Assert.Equal(row.CreatedDate, persisted.CreatedDate);
        Assert.Equal("qualified", persisted.QualificationState);
        Assert.Equal(1, persisted.QualificationVersion);
        Assert.Equal(row.QualificationStateChangedUtc, persisted.QualificationStateChangedUtc);
        Assert.Equal("employee-historical-fixture", audit.ChangedBy);
        Assert.Equal(row.JourneyId, audit.JourneyId);
        Assert.Equal(row.TransactionId, audit.TransactionId);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(done, persisted.Done);
        Assert.Equal("Reviewed", persisted.Message);
        using var detail = await client.GetAsync($"/quotationrequests/{row.Id}");
        using var json = await JsonAsync(detail);
        Assert.Equal("Reviewed", json.RootElement.GetProperty("Message").GetString());
        Assert.Equal(row.JourneyId, json.RootElement.GetProperty("JourneyId").GetGuid());
        Assert.Equal(row.TransactionId, json.RootElement.GetProperty("TransactionId").GetString());
        if (done.HasValue) Assert.Equal(done.Value, json.RootElement.GetProperty("Done").GetBoolean());
        else Assert.False(json.RootElement.TryGetProperty("Done", out _));
    }

    [Fact]
    public async Task Expected_version_conflict_and_malformed_header_do_not_mutate()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var stale = Update(row, true, row.ModifiedDate!.Value.AddDays(-1));
        using var staleResponse = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.Conflict, staleResponse.StatusCode);
        using var malformed = Update(row, true, null);
        malformed.Headers.Add("X-Expected-Modified-Date", "not-a-date");
        using var malformedResponse = await client.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(row.Message, persisted.Message);
        Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
        Assert.Equal(1, await db.RequestQualificationAudit.CountAsync(x => x.RequestId == row.Id));
    }

    [Fact]
    public async Task Concurrent_same_expected_version_has_one_writer_and_one_conflict()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var first = Update(row, true, row.ModifiedDate);
        using var second = Update(row, false, row.ModifiedDate);
        var responses = await Task.WhenAll(client.SendAsync(first), client.SendAsync(second));
        try
        {
            Assert.Equal([HttpStatusCode.NoContent, HttpStatusCode.Conflict], responses.Select(x => x.StatusCode).Order().ToArray());
            await using var db = fixture.Context();
            Assert.Equal(1, await db.Requests.CountAsync(x => x.Id == row.Id));
            Assert.Equal(row.JourneyId, (await db.Requests.SingleAsync(x => x.Id == row.Id)).JourneyId);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Theory]
    [InlineData("GET", false, 401)]
    [InlineData("GET", true, 403)]
    [InlineData("PUT", true, 403)]
    [InlineData("DELETE", true, 403)]
    public async Task Normal_auth_or_live_permission_denial_preserves_graph(string method, bool authenticated, int status)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        await using var app = fixture.App(allowed: false);
        using var client = fixture.Client(app, authenticated);
        using var request = method == "PUT" ? Update(row, true, row.ModifiedDate) : new HttpRequestMessage(new HttpMethod(method), $"/quotationrequests/{row.Id}");
        using var response = await client.SendAsync(request);
        Assert.Equal(status, (int)response.StatusCode);
        await using var db = fixture.Context();
        var persisted = await db.Requests.SingleAsync(x => x.Id == row.Id);
        Assert.Equal(row.Message, persisted.Message);
        Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
        Assert.Equal(1, await db.RequestQualificationAudit.CountAsync(x => x.RequestId == row.Id));
    }

    [Fact]
    public async Task Delete_plain_root_retains_unrelated_metadata_and_durable_create_receipt()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using (var db = fixture.Context())
        {
            db.Files.Add(new() { RequestId = row.Id, Bucket = "synthetic", ObjectName = "fixture-only" });
            db.RequestCreateIdempotency.Add(new() { KeyHash = new string('a', 32) + Guid.NewGuid().ToString("N"), Fingerprint = new string('b', 64), RequestId = row.Id });
            await db.SaveChangesAsync();
        }
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var before = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        using var response = await client.DeleteAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var replay = await client.DeleteAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.NotFound, replay.StatusCode);
        using var detail = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        await using var verify = fixture.Context();
        Assert.False(await verify.Requests.AnyAsync(x => x.Id == row.Id));
        Assert.Equal(1, await verify.Files.CountAsync(x => x.RequestId == row.Id));
        Assert.Equal(1, await verify.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
    }

    [Fact]
    public async Task Delete_audited_root_observes_existing_fk_rejection_without_erasing_history()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.DeleteAsync($"/quotationrequests/{row.Id}");
        await using var db = fixture.Context();
        Assert.True(await db.Requests.AnyAsync(x => x.Id == row.Id));
        Assert.Equal(1, await db.RequestQualificationAudit.CountAsync(x => x.RequestId == row.Id));
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [Fact]
    public async Task Readback_preserves_current_state_but_suppresses_incomplete_attribution_and_pii()
    {
        var complete = await fixture.SeedAsync(Guid.NewGuid().ToString("N"), audited: true);
        var incomplete = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using (var db = fixture.Context())
        {
            (await db.Requests.SingleAsync(x => x.Id == incomplete.Id)).JourneyId = null;
            await db.SaveChangesAsync();
        }
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var response = await client.GetAsync($"/quotationrequests/qualification-outcomes/readback?fromUtc={Uri.EscapeDataString(DateTime.UtcNow.AddDays(-4).ToString("O"))}&toUtc={Uri.EscapeDataString(DateTime.UtcNow.AddMinutes(-1).ToString("O"))}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = await JsonAsync(response);
        var rows = json.RootElement.GetProperty("requests").EnumerateArray().ToArray();
        var attributed = Assert.Single(rows, x => x.GetProperty("requestId").GetInt32() == complete.Id);
        Assert.Equal("qualified", attributed.GetProperty("state").GetString());
        Assert.Equal(complete.JourneyId, attributed.GetProperty("journeyId").GetGuid());
        Assert.Equal(complete.TransactionId, attributed.GetProperty("transactionId").GetString());
        Assert.Equal(["createdUtc", "journeyId", "requestId", "state", "transactionId"], attributed.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var unattributed = Assert.Single(rows, x => x.GetProperty("requestId").GetInt32() == incomplete.Id);
        Assert.False(unattributed.TryGetProperty("journeyId", out _));
        Assert.False(unattributed.TryGetProperty("transactionId", out _));
        Assert.Equal(["createdUtc", "requestId", "state"], unattributed.EnumerateObject().Select(x => x.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("nonemployee", 403)]
    [InlineData("live-denied", 403)]
    [InlineData("reversed", 400)]
    [InlineData("future", 400)]
    [InlineData("too-wide", 400)]
    [InlineData("missing-utc", 400)]
    public async Task Readback_denies_wrong_admission_or_invalid_window(string scenario, int expected)
    {
        await using var app = fixture.App(allowed: scenario != "live-denied");
        using var client = fixture.Client(app, employee: scenario != "nonemployee");
        var from = DateTime.UtcNow.AddDays(-2);
        var to = DateTime.UtcNow.AddMinutes(-1);
        if (scenario == "reversed") from = to.AddDays(1);
        if (scenario == "future") to = DateTime.UtcNow.AddDays(1);
        if (scenario == "too-wide") from = to.AddDays(-32);
        var fromText = scenario == "missing-utc" ? DateTime.SpecifyKind(from, DateTimeKind.Unspecified).ToString("O") : from.ToString("O");
        using var response = await client.GetAsync($"/quotationrequests/qualification-outcomes/readback?fromUtc={Uri.EscapeDataString(fromText)}&toUtc={Uri.EscapeDataString(to.ToString("O"))}");
        Assert.Equal(expected, (int)response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_failure_before_explicit_commit_retries_only_after_rollback_in_fresh_context(bool delete)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var fault = new RequestTriageAckFault(delete);
        var rollback = new RequestCreateRollbackObserver();
        await using var app = fixture.App(interceptors: [fault, rollback]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        // Observe durable graph and reached command counts BEFORE response assertions.
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
        var receipts = await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id);
        Assert.True(fault.Fired, "Synthetic fault did not reach the actual executed mutation.");
        Assert.False(fault.Autocommit);
        if (delete) Assert.Null(persisted);
        else Assert.Equal("Reviewed", persisted!.Message);
        Assert.Equal(0, receipts);
        Assert.Equal(1, rollback.Rollbacks);
        Assert.Equal(2, fault.Executions);
        Assert.Equal(2, fault.Contexts.Count);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redis_eviction_denial_cannot_override_postgres_authoritative_detail(bool delete)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var cached = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(HttpStatusCode.OK, cached.StatusCode);
        var old = await cached.Content.ReadFromJsonAsync<QuotationRequestResponse>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(old);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
            await cache.SetAsync($"request:{row.Id}", old, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(old, await cache.GetAsync<QuotationRequestResponse>($"request:{row.Id}", CancellationToken.None));
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(fixture.RedisConnection);
        var database = redis.GetDatabase();
        await database.ExecuteAsync("ACL", "SETUSER", "default", "-del", "-unlink");
        try
        {
            // Prove real Redis denial, not a substituted cache implementation.
            await Assert.ThrowsAsync<RedisServerException>(() => database.KeyDeleteAsync("synthetic-denial-probe"));
            using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            await using var db = fixture.Context();
            var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
            if (delete) Assert.Null(persisted);
            else Assert.Equal("Reviewed", persisted!.Message);
            using var detail = await client.GetAsync($"/quotationrequests/{row.Id}");
            Assert.Equal(delete ? HttpStatusCode.NotFound : HttpStatusCode.OK, detail.StatusCode);
            if (!delete)
            {
                using var json = await JsonAsync(detail);
                Assert.Equal("Reviewed", json.RootElement.GetProperty("Message").GetString());
            }
        }
        finally { await database.ExecuteAsync("ACL", "SETUSER", "default", "+del", "+unlink"); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_cache_writer_cannot_poison_updated_or_deleted_detail(bool delete)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        await using var app = fixture.App();
        using var client = fixture.Client(app);
        using var before = await client.GetAsync($"/quotationrequests/{row.Id}");
        var old = await before.Content.ReadFromJsonAsync<QuotationRequestResponse>();
        Assert.NotNull(old);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using (var scope = app.Services.CreateScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<IQuotationCache>();
            await cache.SetAsync($"request:{row.Id}", old, TimeSpan.FromMinutes(2), CancellationToken.None);
            Assert.Equal(old, await cache.GetAsync<QuotationRequestResponse>($"request:{row.Id}", CancellationToken.None));
        }
        using var detail = await client.GetAsync($"/quotationrequests/{row.Id}");
        Assert.Equal(delete ? HttpStatusCode.NotFound : HttpStatusCode.OK, detail.StatusCode);
        if (!delete)
        {
            using var json = await JsonAsync(detail);
            Assert.Equal("Reviewed", json.RootElement.GetProperty("Message").GetString());
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lost_commit_ack_requires_single_commit_and_generic_unavailable(bool delete, bool arbitrary)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var transient = new RequestCreateCommitFault();
        var nonprovider = new RequestCreateArbitraryCommitFault();
        await using var app = fixture.App(interceptors: [arbitrary ? nonprovider : transient]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
        Assert.True(arbitrary ? nonprovider.Fired : transient.Fired);
        Assert.Equal(1, arbitrary ? nonprovider.Commits : transient.CommitAcknowledgements);
        if (delete) Assert.Null(persisted);
        else Assert.Equal("Reviewed", persisted!.Message);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var json = await JsonAsync(response);
        Assert.Equal("request_mutation_unavailable", json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("Synthetic", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Postcommit_disposal_failure_cannot_replay_receiptless_mutation(bool delete)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var fault = new RequestCreateDisposalFault();
        await using var app = fixture.App(interceptors: [fault.Commit, fault.Disposal]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        Assert.True(fault.Fired);
        Assert.Equal(1, fault.Commits);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
        if (delete) Assert.Null(persisted);
        else Assert.Equal("Reviewed", persisted!.Message);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Rollback_or_cleanup_failure_preserves_both_internal_causes_without_replay(bool delete, bool teardown)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var command = new RequestTriageAckFault(delete);
        var rollback = new RequestCreateRollbackObserver(!teardown);
        var cleanup = new RequestCreateRollbackTeardownFault(rollback);
        var retained = false;
        var busyReader = false;
        void Observe(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (command.Fired && args.Exception.GetType().FullName == "Npgsql.NpgsqlOperationInProgressException") busyReader = true;
            var second = teardown ? cleanup.Failure : rollback.Failure;
            if (command.Failure is not null && second is not null && Contains(args.Exception, command.Failure) && Contains(args.Exception, second)) retained = true;
        }
        await using var app = fixture.App(interceptors: teardown ? [command, rollback, cleanup] : [command, rollback]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            using var response = await client.SendAsync(request);
            Assert.True(command.Fired);
            Assert.True(teardown ? cleanup.Fired : rollback.Fired, $"Rollback/cleanup fault not reached; busyReader={busyReader}.");
            Assert.True(retained, "Exact synthetic original and rollback/cleanup causes were not retained internally.");
            Assert.Equal(1, command.Executions);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            await using var db = fixture.Context();
            var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(row.Message, persisted.Message);
            Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
            Assert.Equal(0, await db.RequestCreateIdempotency.CountAsync(x => x.RequestId == row.Id));
            Assert.DoesNotContain("Synthetic", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Caller_cancellation_propagates_with_correct_pre_or_postcommit_graph(bool delete, bool afterCommit)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        using var cancel = new CancellationTokenSource();
        var command = new RequestTriageAckFault(delete, cancel);
        var commit = new RequestCreateCommitFault(cancel);
        await using var app = fixture.App(interceptors: [afterCommit ? commit : command]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.SendAsync(request, cancel.Token));
        Assert.True(afterCommit ? commit.Fired : command.Fired);
        if (afterCommit) Assert.Equal(1, commit.CommitAcknowledgements);
        else Assert.Equal(1, command.Executions);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.Id);
        if (afterCommit && delete) Assert.Null(persisted);
        else
        {
            Assert.NotNull(persisted);
            Assert.Equal(afterCommit ? "Reviewed" : row.Message, persisted.Message);
            if (!afterCommit) Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
        }
    }

    private static bool Contains(Exception exception, Exception expected, int depth = 0)
    {
        if (ReferenceEquals(exception, expected)) return true;
        if (depth == 8) return false;
        if (exception is AggregateException aggregate) return aggregate.InnerExceptions.Any(x => Contains(x, expected, depth + 1));
        return exception.InnerException is not null && Contains(exception.InnerException, expected, depth + 1);
    }

    [Fact]
    public async Task Direct_precommit_business_failure_remains400_without_any_write_or_retry()
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        var fault = new RequestTriageBusinessFault();
        var rollback = new RequestCreateRollbackObserver();
        await using var app = fixture.App(interceptors: [fault, rollback]);
        using var client = fixture.Client(app);
        using var request = Update(row, true, row.ModifiedDate);
        using var response = await client.SendAsync(request);
        Assert.Equal(1, fault.Saves);
        Assert.Equal(1, rollback.Rollbacks);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await using var db = fixture.Context();
        var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
        Assert.Equal(row.Message, persisted.Message);
        Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_root_outcome_cleanup_failure_is_unavailable_not_a_business400(bool delete)
    {
        var rollback = new RequestCreateRollbackObserver();
        var cleanup = new RequestCreateRollbackTeardownFault(rollback);
        await using var app = fixture.App(interceptors: [rollback, cleanup]);
        using var client = fixture.Client(app);
        var missing = new QuotationRequest { Id = int.MaxValue };
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, "/quotationrequests/2147483647") : Update(missing, true, null);
        using var response = await client.SendAsync(request);
        Assert.Equal(1, rollback.Rollbacks);
        Assert.True(cleanup.Fired);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        await using var db = fixture.Context();
        Assert.False(await db.Requests.AnyAsync(x => x.Id == int.MaxValue));
    }

    [Theory]
    [InlineData(false, false, 500)]
    [InlineData(false, true, 500)]
    [InlineData(true, false, 500)]
    [InlineData(true, true, 400)]
    public async Task Unretryable_precommit_failure_preserves_existing_category_and_rolls_back(bool delete, bool business, int status)
    {
        var row = await fixture.SeedAsync(Guid.NewGuid().ToString("N"));
        Exception cause = business ? new InvalidOperationException("Synthetic precommit business failure.") : new PostgresException("Synthetic precommit rejection.", "ERROR", "ERROR", "P0001");
        var fault = new RequestTriageAckFault(delete, failure: cause);
        var rollback = new RequestCreateRollbackObserver();
        await using var app = fixture.App(interceptors: [fault, rollback]);
        using var client = fixture.Client(app);
        using var request = delete ? new HttpRequestMessage(HttpMethod.Delete, $"/quotationrequests/{row.Id}") : Update(row, true, row.ModifiedDate);
        var wrapped = false;
        void Observe(object? sender, FirstChanceExceptionEventArgs args)
        {
            if (args.Exception is DbUpdateException && Contains(args.Exception, cause)) wrapped = true;
        }
        AppDomain.CurrentDomain.FirstChanceException += Observe;
        try
        {
            using var response = await client.SendAsync(request);
            Assert.True(fault.Fired);
            Assert.Equal(1, fault.Executions);
            Assert.Equal(1, rollback.Rollbacks);
            await using var db = fixture.Context();
            var persisted = await db.Requests.AsNoTracking().SingleAsync(x => x.Id == row.Id);
            Assert.Equal(row.Message, persisted.Message);
            Assert.Equal(row.ModifiedDate, persisted.ModifiedDate);
            Assert.True(status == (int)response.StatusCode, $"Expected={status}; actual={(int)response.StatusCode}; exactSyntheticCauseWrappedByDbUpdateException={wrapped}.");
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= Observe; }
    }

    private static HttpRequestMessage Update(QuotationRequest row, bool? done, DateTime? expected)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/quotationrequests/{row.Id}")
        {
            Content = JsonContent.Create(new
            {
                FirstName = "Reviewed",
                LastName = "Fixture",
                Email = "updated@example.invalid",
                TelephoneNumber = "0000",
                Country = "Thailand",
                CompanyName = "Synthetic",
                TaxIdentification = "fixture",
                Message = "Reviewed",
                InternalComment = "review-only",
                Done = done,
                JourneyId = Guid.NewGuid(),
                TransactionId = "client-forged",
                CreatedDate = DateTime.UtcNow,
                QualificationState = "not_qualified",
                QualificationVersion = 99
            })
        };
        if (expected.HasValue) request.Headers.Add("X-Expected-Modified-Date", DateTime.SpecifyKind(expected.Value, DateTimeKind.Utc).ToString("O"));
        return request;
    }

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response)
    {
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
    private static int[] Ids(JsonDocument json) => json.RootElement.GetProperty("Items").EnumerateArray().Select(x => x.GetProperty("Id").GetInt32()).ToArray();
}

public sealed class RequestTriageBusinessFault : SaveChangesInterceptor
{
    public int Saves { get; private set; }
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Saves++;
        throw new InvalidOperationException("Synthetic direct precommit business failure.");
    }
}

public sealed class RequestTriageFixture : IAsyncLifetime
{
    private PostgreSqlContainer postgres = null!;
    private IContainer redis = null!;
    private Infrastructure.DisposableContainerPair? containers;
    private readonly RSA key = RSA.Create(2048);
    private string RequestConnection => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = "request_triage" }.ConnectionString;
    public string RedisConnection => $"{redis.Hostname}:{redis.GetMappedPublicPort(6379)}";

    public async Task InitializeAsync()
    {
        containers = await Infrastructure.DisposableContainerPair.StartAsync("quotation97-triage",
            attempt => new PostgreSqlBuilder("postgres:18-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).Build(),
            attempt => new ContainerBuilder("redis:7-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).WithPortBinding(6379, true)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6379)).Build());
        postgres = (PostgreSqlContainer)containers.First;
        redis = containers.Second;
        await using (var connection = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE request_triage", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var quotation = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(postgres.GetConnectionString()).Options);
        await quotation.Database.MigrateAsync();
        await using var requests = Context();
        await requests.Database.MigrateAsync();
    }

    public QuotationRequestDbContext Context() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(RequestConnection).Options);

    public async Task<QuotationRequest> SeedAsync(string marker, DateTime? date = null, bool audited = false, string? field = null, string? value = null)
    {
        // Existing historical graph, fixture-only; no invented authorization/session grants.
        await using var db = Context();
        var timestamp = DateTime.SpecifyKind(date ?? DateTime.UtcNow.AddDays(-1), DateTimeKind.Unspecified);
        timestamp = timestamp.AddTicks(-(timestamp.Ticks % 10));
        var row = new QuotationRequest { FirstName = "Synthetic", Message = marker, JourneyId = Guid.NewGuid(), CreatedDate = timestamp, ModifiedDate = timestamp, Done = false };
        if (field is not null) typeof(QuotationRequest).GetProperty(field)!.SetValue(row, value);
        db.Requests.Add(row);
        await db.SaveChangesAsync();
        row.TransactionId = $"request-{row.Id}";
        if (audited)
        {
            row.QualificationState = "qualified"; row.QualificationVersion = 1; row.QualificationStateChangedUtc = timestamp;
            db.RequestQualificationAudit.Add(new()
            {
                RequestId = row.Id,
                JourneyId = row.JourneyId,
                TransactionId = row.TransactionId,
                IdempotencyKey = Guid.NewGuid().ToString("N"),
                PreviousState = "unreviewed",
                NewState = "qualified",
                ChangedBy = "employee-historical-fixture",
                ChangedUtc = timestamp,
                Version = 1
            });
        }
        await db.SaveChangesAsync();
        return row;
    }

    public HttpClient Client(WebApplicationFactory<Program> app, bool authenticated = true, bool employee = true)
    {
        var client = app.CreateClient();
        if (authenticated)
        {
            var claims = new List<Claim> { new("sub", "employee-triage-fixture"), new("identity_kind", employee ? "employee" : "customer") };
            if (employee) claims.Add(new("role", "Employee"));
            foreach (var permission in new[] { "read", "update", "delete" }) claims.Add(new("permissions", "legacy.quotation-requests." + permission));
            var jwt = new JwtSecurityToken("https://triage-auth.example", "triage-services", claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256));
            client.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(jwt));
        }
        return client;
    }

    public WebApplicationFactory<Program> App(bool allowed = true, params IInterceptor[] interceptors) => new Factory(new Dictionary<string, string?>
    {
        ["ConnectionStrings:QuotationDbContext"] = postgres.GetConnectionString(),
        ["ConnectionStrings:QuotationRequestDbContext"] = RequestConnection,
        ["ConnectionStrings:redis"] = RedisConnection,
        ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(key.ExportSubjectPublicKeyInfoPem())),
        ["Jwt:Issuer"] = "https://triage-auth.example",
        ["Jwt:Audience"] = "triage-services",
        ["IAM:LivePermissionChecks:Credential"] = "synthetic-triage-live-check",
        ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "",
        ["Observability:RuntimeMetricsEnabled"] = "false"
    }, allowed, interceptors);

    public async Task DisposeAsync()
    {
        if (containers is not null) await containers.DisposeAsync();
        key.Dispose();
    }

    private sealed class Factory(Dictionary<string, string?> settings, bool allowed, IInterceptor[] interceptors) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            QuotationTestWorkloadExchange.Prepare(builder);
            foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureTestServices(services =>
            {
                if (interceptors.Length > 0) services.AddDbContext<QuotationRequestDbContext>(options => options.AddInterceptors(interceptors));
                services.AddScoped<IIamServiceClient, IamServiceClient>();
                services.AddHttpClient("IAMService", client => client.BaseAddress = new Uri("https://triage-iam.example"))
                    .ConfigurePrimaryHttpMessageHandler(() => new Transport(allowed));
            });
        }
    }

    private sealed class Transport(bool allowed) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            QuotationTestWorkloadExchange.AssertQuotationSubject(request);
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/iam/v1/auth/check-permission", request.RequestUri!.AbsolutePath);
            Assert.Equal("synthetic-triage-live-check", Assert.Single(request.Headers.GetValues("X-Maliev-IAM-Live-Check-Key")));
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("employee-triage-fixture", json.RootElement.GetProperty("principalId").GetString());
            Assert.Contains(json.RootElement.GetProperty("permissionId").GetString(), new[] { "legacy.quotation-requests.read", "legacy.quotation-requests.update", "legacy.quotation-requests.delete" });
            Assert.True(json.RootElement.GetProperty("bypassCache").GetBoolean());
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed }) };
        }
    }
}

/// <summary>Throws once after normal Npgsql command execution; reports whether the command was autocommitted.</summary>
public sealed class RequestTriageAckFault(bool delete, CancellationTokenSource? cancel = null, Exception? failure = null) : DbCommandInterceptor
{
    public int Executions { get; private set; }
    public bool Fired { get; private set; }
    public bool Autocommit { get; private set; }
    public HashSet<Guid> Contexts { get; } = [];
    public Exception? Failure { get; private set; }

    private void Observe(DbCommand command)
    {
        var expected = delete ? "DELETE FROM \"Request\"" : "UPDATE \"Request\"";
        if (!command.CommandText.StartsWith(expected, StringComparison.Ordinal)) return;
        Executions++;
        if (Fired) return;
        Fired = true;
        Autocommit = command.Transaction is null;
        if (cancel is not null) { cancel.Cancel(); throw new OperationCanceledException(cancel.Token); }
        Failure = failure ?? new NpgsqlException("Synthetic triage command acknowledgement loss.", new IOException("Synthetic interruption."));
        throw Failure;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        int result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.StartsWith(delete ? "DELETE FROM \"Request\"" : "UPDATE \"Request\"", StringComparison.Ordinal))
            Contexts.Add(eventData.Context!.ContextId.InstanceId);
        Observe(command);
        return ValueTask.FromResult(result);
    }

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
        DbDataReader result, CancellationToken cancellationToken = default)
    {
        if (command.CommandText.StartsWith(delete ? "DELETE FROM \"Request\"" : "UPDATE \"Request\"", StringComparison.Ordinal))
        {
            Contexts.Add(eventData.Context!.ContextId.InstanceId);
            // Throwing before EF receives the reader transfers cleanup responsibility to this fixture.
            if (!Fired) await result.DisposeAsync();
        }
        Observe(command);
        return result;
    }
}
