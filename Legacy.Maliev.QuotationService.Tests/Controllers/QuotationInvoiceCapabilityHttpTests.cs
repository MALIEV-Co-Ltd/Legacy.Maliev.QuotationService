using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Normal Production Program recipient/PG readback with controlled signed proofs and live IAM transport; not Auth/Accounting/Order parity.</summary>
public sealed class QuotationInvoiceCapabilityHttpTests(QuotationNormalIamFixture fixture) : IClassFixture<QuotationNormalIamFixture>
{
    private readonly ConcurrentDictionary<Guid, Dictionary<string, object>> financialOperations = new();
    private readonly ConcurrentQueue<(HttpMethod Method, string Path, string? Workload)> financialReads = new();

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoundEmployeeCompletion_ReachesExistingRepositoryWithoutInventingHistoricalOutcome(bool? accepted)
    {
        var row = await fixture.SeedAsync(accepted);
        var transport = new LiveTransport();
        await using var app = App(transport);
        using var client = fixture.Client(app);
        using var request = Decision(row.Id, row.ModifiedDate!.Value, "bound");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertWire(transport, row.Id, employeeExpected: true);
        await using var db = fixture.Context();
        var stored = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id);
        Assert.Equal(901, stored.InvoiceId);
        Assert.True(stored.Accepted);
        var outcomes = await db.AcceptedOutcomes.Where(value => value.QuotationId == row.Id).ToArrayAsync();
        if (accepted == true)
        {
            Assert.Null(stored.AcceptedUtc);
            Assert.Null(stored.AcceptanceOrigin);
            Assert.Empty(outcomes);
            Assert.Equal(row.ModifiedDate, stored.DecisionOrderVersion);
        }
        else
        {
            Assert.NotNull(stored.AcceptedUtc);
            Assert.Equal("employee", stored.AcceptanceOrigin);
            Assert.Equal("employee", Assert.Single(outcomes).AcceptanceOrigin);
        }
        Assert.Empty(await db.GoogleAnalyticsOutbox.Where(value => value.QuotationId == row.Id).ToArrayAsync());
        var read = Assert.Single(financialReads);
        Assert.Equal(HttpMethod.Get, read.Method);
        Assert.StartsWith("/internal/invoice-creation/operations/", read.Path, StringComparison.Ordinal);
        Assert.EndsWith("/financial-ownership", read.Path, StringComparison.Ordinal);
        Assert.Equal("service:legacy-quotation", read.Workload);
    }

    [Theory]
    [InlineData("unbound")]
    [InlineData("other-invoice")]
    [InlineData("other-version")]
    [InlineData("bad-binding")]
    [InlineData("missing")]
    public async Task InvalidBoundAuthority_RetainsBareAccountingDenialAndExactStoredJson(string mode)
    {
        var row = await fixture.SeedAsync(true);
        var before = await Snapshot(row.Id);
        var transport = new LiveTransport();
        await using var app = App(transport);
        using var client = fixture.Client(app);
        using var request = Decision(row.Id, row.ModifiedDate!.Value, mode);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertWire(transport, row.Id, employeeExpected: false);
        Assert.Equal(before, await Snapshot(row.Id));
        Assert.Empty(financialReads);
    }

    [Fact]
    public async Task LateAttachmentAndSameOperationReplay_PreserveFirstCustomerOutcomeAndOrderVersion()
    {
        var row = await fixture.SeedAsync(true);
        var version = row.ModifiedDate!.Value;
        var acceptedTime = version.AddHours(-1);
        await using (var db = fixture.Context())
        {
            var stored = await db.Quotations.FindAsync(row.Id);
            stored!.AcceptedUtc = acceptedTime;
            stored.AcceptanceOrigin = "customer";
            db.AcceptedOutcomes.Add(new Legacy.Maliev.QuotationService.Domain.QuotationAcceptedOutcome
            {
                EventKey = $"quotation-{row.Id}:accepted:v1",
                QuotationId = row.Id,
                AcceptedUtc = acceptedTime,
                AcceptanceOrigin = "customer",
            });
            await db.SaveChangesAsync();
        }
        var transport = new LiveTransport();
        await using var app = App(transport);
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, version, "bound", operation);
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var linkedSnapshot = await Snapshot(row.Id);
        using var replay = Decision(row.Id, version, "bound", operation);
        using var replayResponse = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(linkedSnapshot, await Snapshot(row.Id));
        await using var readback = fixture.Context();
        var current = await readback.Quotations.AsNoTracking().SingleAsync(value => value.Id == row.Id);
        Assert.Equal(acceptedTime, current.AcceptedUtc);
        Assert.Equal("customer", current.AcceptanceOrigin);
        Assert.Equal(row.ModifiedDate, current.DecisionOrderVersion);
        var outcome = Assert.Single(await readback.AcceptedOutcomes.Where(value => value.QuotationId == row.Id).ToArrayAsync());
        Assert.Equal(acceptedTime, outcome.AcceptedUtc);
        Assert.Equal("customer", outcome.AcceptanceOrigin);
        Assert.Equal(4, transport.Records.Count);
    }

    [Theory]
    [InlineData("stale-version")]
    [InlineData("existing-other-invoice")]
    public async Task ValidAuthority_CannotRelaxExistingRepositoryConflicts(string mode)
    {
        var row = await fixture.SeedAsync(true);
        if (mode == "existing-other-invoice")
        {
            await using var db = fixture.Context();
            var stored = await db.Quotations.FindAsync(row.Id);
            stored!.InvoiceId = 902;
            await db.SaveChangesAsync();
        }
        var before = await Snapshot(row.Id);
        var transport = new LiveTransport();
        await using var app = App(transport);
        using var client = fixture.Client(app);
        using var request = Decision(row.Id, mode == "stale-version" ? row.ModifiedDate!.Value.AddSeconds(-1) : row.ModifiedDate!.Value, "bound");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertWire(transport, row.Id, employeeExpected: true);
        Assert.Equal(before, await Snapshot(row.Id));
    }

    [Fact]
    public async Task FinancialDriftAfterMint_ConflictsWithoutLocalInvoiceAttachment()
    {
        var row = await fixture.SeedAsync(true);
        var before = await Snapshot(row.Id);
        var transport = new LiveTransport();
        await using var app = App(transport, financialMode: "drift");
        using var client = fixture.Client(app);
        using var request = Decision(row.Id, row.ModifiedDate!.Value, "bound");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertWire(transport, row.Id, employeeExpected: true);
        Assert.Single(financialReads);
        Assert.Equal(before, await Snapshot(row.Id));
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("transport")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("type")]
    [InlineData("oversized")]
    public async Task UnavailableOrMalformedFinancialReadback_FailsClosedWithoutMutation(string mode)
    {
        var row = await fixture.SeedAsync(true);
        var before = await Snapshot(row.Id);
        var transport = new LiveTransport();
        await using var app = App(transport, financialMode: mode);
        using var client = fixture.Client(app);
        using var request = Decision(row.Id, row.ModifiedDate!.Value, "bound");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Single(financialReads);
        Assert.Equal(before, await Snapshot(row.Id));
    }

    [Fact]
    public async Task LiveEmployeeDenial_HasNoServiceClaimFallbackAndNoMutation()
    {
        var row = await fixture.SeedAsync(true);
        var before = await Snapshot(row.Id);
        var transport = new LiveTransport { DenyEmployee = true };
        await using var app = App(transport);
        using var client = fixture.Client(app);
        using var request = Decision(row.Id, row.ModifiedDate!.Value, "bound");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        AssertWire(transport, row.Id, employeeExpected: true);
        Assert.Equal(before, await Snapshot(row.Id));
    }

    [Fact]
    public async Task CapabilityExpiresAcrossLiveAwait_NoMutationAfterFreshServiceAllow()
    {
        var row = await fixture.SeedAsync(true);
        var before = await Snapshot(row.Id);
        var clock = new MutableClock();
        var transport = new LiveTransport { HoldEmployeeResponse = true };
        await using var app = App(transport, clock);
        using var client = fixture.Client(app);
        using var request = Decision(row.Id, row.ModifiedDate!.Value, "bound", proofClock: clock);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = client.SendAsync(request, caller.Token);
        try
        {
            await transport.EmployeeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            clock.Advance(TimeSpan.FromMinutes(3));
            transport.EmployeeRelease.TrySetResult();
            using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            AssertWire(transport, row.Id, employeeExpected: true);
            Assert.Empty(financialReads);
            Assert.Equal(before, await Snapshot(row.Id));
        }
        finally
        {
            transport.EmployeeRelease.TrySetResult();
            await DrainPendingAsync(pending, caller);
        }
    }

    [Fact]
    public async Task OriginalCallerCancellationAtEmployeeCheck_PropagatesWithoutMutation()
    {
        var row = await fixture.SeedAsync(true);
        var before = await Snapshot(row.Id);
        var transport = new LiveTransport { WaitForEmployeeCancellation = true };
        await using var app = App(transport);
        using var client = fixture.Client(app);
        using var request = Decision(row.Id, row.ModifiedDate!.Value, "bound");
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = client.SendAsync(request, caller.Token);
        try
        {
            await transport.EmployeeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await pending; });
            await transport.EmployeeCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AssertWire(transport, row.Id, employeeExpected: true);
        }
        finally
        {
            await DrainPendingAsync(pending, caller);
        }
        Assert.Equal(before, await Snapshot(row.Id));
    }

    [Fact]
    public async Task LostDecisionAcknowledgement_ReadsExactCompletedReceipt_ThenReplaySkipsOrders()
    {
        var row = await fixture.SeedAsync(true);
        await LinkOrders(row.Id, 71, 72);
        var orders = new ReceiptOrders();
        await using var app = App(new LiveTransport(), orderClient: orders);
        using var authenticated = fixture.Client(app);
        using var client = new HttpClient(new LostAck { InnerHandler = app.Server.CreateHandler() }) { BaseAddress = authenticated.BaseAddress };
        client.DefaultRequestHeaders.Authorization = authenticated.DefaultRequestHeaders.Authorization;
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(first));
        using var read = ReadOperation(row.Id, row.ModifiedDate.Value, operation);
        using var response = await client.SendAsync(read);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var receipt = await response.Content.ReadFromJsonAsync<QuotationInvoiceCompletionReceipt>();
        Assert.NotNull(receipt);
        Assert.Equal("Completed", receipt.State);
        Assert.Equal(operation.ToString("D"), receipt.OperationId);
        Assert.Equal(2, receipt.CompletedOrders);
        Assert.Equal(2, receipt.TotalOrders);
        Assert.Equal(2, orders.Calls.Count);
        using var replay = Decision(row.Id, row.ModifiedDate.Value, "bound", operation);
        using var replayResponse = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(2, orders.Calls.Count);
        var replayResult = await replayResponse.Content.ReadFromJsonAsync<QuotationDecisionResponse>();
        Assert.Equal(receipt.ModifiedDate, DateTime.SpecifyKind(replayResult!.ModifiedDate!.Value, DateTimeKind.Utc).ToString("O"));
    }

    [Theory]
    [InlineData(OrderDecisionResult.Unavailable, "OrdersPartial", HttpStatusCode.ServiceUnavailable)]
    [InlineData(OrderDecisionResult.Conflict, "OrdersConflict", HttpStatusCode.Conflict)]
    public async Task PartialOrderFailure_ReadbackIsPartial_ResumeSkipsCheckpointedOrders(
        OrderDecisionResult failure, string state, HttpStatusCode status)
    {
        var row = await fixture.SeedAsync(true);
        await LinkOrders(row.Id, 81, 82);
        var orders = new ReceiptOrders { FailOrder = 82, Failure = failure };
        await using var app = App(new LiveTransport(), orderClient: orders);
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(status, firstResponse.StatusCode);
        using var read = ReadOperation(row.Id, row.ModifiedDate.Value, operation);
        using var response = await client.SendAsync(read);
        var receipt = await response.Content.ReadFromJsonAsync<QuotationInvoiceCompletionReceipt>();
        Assert.Equal(state, receipt!.State);
        Assert.Equal(1, receipt.CompletedOrders);
        Assert.Equal(2, receipt.TotalOrders);
        orders.FailOrder = null;
        using var resume = Decision(row.Id, row.ModifiedDate.Value, "bound", operation);
        using var resumed = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Single(orders.Calls, x => x.Order == 81);
        var retries = orders.Calls.Where(x => x.Order == 82).ToArray();
        Assert.Equal(2, retries.Length);
        Assert.Equal(retries[0].Key, retries[1].Key);
        await using var db = fixture.Context();
        Assert.Equal("Completed", (await db.InvoiceCompletionOperations.SingleAsync(x => x.OperationId == operation)).State);
    }

    [Fact]
    public async Task CallerCancellationAfterFirstOrder_PreservesPartialReceiptAndFiniteClaimRecovery()
    {
        var row = await fixture.SeedAsync(true);
        await LinkOrders(row.Id, 91, 92);
        var orders = new ReceiptOrders { HoldOrder = 92 };
        await using var app = App(new LiveTransport(), orderClient: orders);
        using var client = fixture.Client(app);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        var pending = client.SendAsync(first, caller.Token);
        try
        {
            await orders.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var result = await pending; });
            await orders.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await DrainPendingAsync(pending, caller); }
        await using var db = fixture.Context();
        var retained = await db.InvoiceCompletionOperations.AsNoTracking().SingleAsync(x => x.OperationId == operation);
        Assert.Equal("OrdersPartial", retained.State);
        Assert.Equal("[91]", retained.CompletedOrderIdsJson);
        Assert.True(retained.ClaimId is null || retained.ClaimUntil is not null);
        orders.HoldOrder = null;
        // Observe the actual server-finally release rather than clearing ownership from the test.
        using (var releaseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            while (true)
            {
                await using var observation = fixture.Context();
                var claim = await observation.InvoiceCompletionOperations.AsNoTracking()
                    .Where(x => x.OperationId == operation).Select(x => x.ClaimId).SingleAsync(releaseDeadline.Token);
                if (claim is null) break;
                await Task.Delay(TimeSpan.FromMilliseconds(25), releaseDeadline.Token);
            }
        }
        using var resume = Decision(row.Id, row.ModifiedDate.Value, "bound", operation);
        using var resumed = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.OK, resumed.StatusCode);
        Assert.Single(orders.Calls, x => x.Order == 91);
    }

    [Fact]
    public async Task SameInvoiceWithoutOwningOperation_IsNotAdoptedOrAcknowledged()
    {
        var row = await fixture.SeedAsync(true);
        await using (var db = fixture.Context())
        {
            var entity = await db.Quotations.FindAsync(row.Id);
            entity!.InvoiceId = 901;
            await db.SaveChangesAsync();
        }
        await using var app = App(new LiveTransport());
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var read = ReadOperation(row.Id, row.ModifiedDate!.Value, operation);
        using var response = await client.SendAsync(read);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var decision = Decision(row.Id, row.ModifiedDate.Value, "bound", operation);
        using var conflict = await client.SendAsync(decision);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        await using var verify = fixture.Context();
        Assert.Empty(await verify.InvoiceCompletionOperations.Where(x => x.QuotationId == row.Id).ToArrayAsync());
    }

    [Fact]
    public async Task OperationInsertFailure_RollsBackQuotationLinkAndAcceptance()
    {
        var row = await fixture.SeedAsync(null);
        var before = await Snapshot(row.Id);
        await using var db = fixture.Context();
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationInvoiceCompletionOperation\" ADD CONSTRAINT \"receipt_test_reject_insert\" CHECK (\"InvoiceId\" < 0) NOT VALID");
        try
        {
            await using var app = App(new LiveTransport());
            using var client = fixture.Client(app);
            var operation = Guid.NewGuid();
            using var decision = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
            using var failed = await client.SendAsync(decision);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.DoesNotContain("receipt_test_reject_insert", await failed.Content.ReadAsStringAsync());
            using var read = ReadOperation(row.Id, row.ModifiedDate.Value, operation);
            using var absent = await client.SendAsync(read);
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"QuotationInvoiceCompletionOperation\" DROP CONSTRAINT \"receipt_test_reject_insert\"");
        }
        Assert.Equal(before, await Snapshot(row.Id));
    }

    [Fact]
    public async Task ConcurrentClaimAndExpiredOwner_AreConditionalAndCannotOverwriteNewOwner()
    {
        var row = await fixture.SeedAsync(true);
        await LinkOrders(row.Id, 101);
        var clock = new MutableClock();
        await using var app = App(new LiveTransport(), clock);
        await using var firstScope = app.Services.CreateAsyncScope();
        await using var secondScope = app.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IQuotationInvoiceCompletionStore>();
        var second = secondScope.ServiceProvider.GetRequiredService<IQuotationInvoiceCompletionStore>();
        var authority = Context(row.Id, row.ModifiedDate!.Value);
        Assert.Equal(QuotationDecisionPersistenceStatus.Completed, (await first.ApplyAsync(authority, CancellationToken.None)).Status);
        var firstClaim = Guid.NewGuid();
        var secondClaim = Guid.NewGuid();
        var acquired = await Task.WhenAll(first.ClaimAsync(authority.OperationId, firstClaim, CancellationToken.None),
            second.ClaimAsync(authority.OperationId, secondClaim, CancellationToken.None));
        Assert.Single(acquired, x => x);
        var old = acquired[0] ? firstClaim : secondClaim;
        clock.Advance(TimeSpan.FromMinutes(3));
        var replacement = Guid.NewGuid();
        Assert.True(await second.ClaimAsync(authority.OperationId, replacement, CancellationToken.None));
        Assert.False(await first.CheckpointAsync(authority.OperationId, old, [101], "Completed", CancellationToken.None));
        Assert.True(await second.CheckpointAsync(authority.OperationId, replacement, [101], "Completed", CancellationToken.None));
        await first.ReleaseAsync(authority.OperationId, old, CancellationToken.None);
        Assert.Equal("Completed", (await second.ReadAsync(authority.OperationId, CancellationToken.None))!.Receipt.State);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("employee")]
    [InlineData("issuer")]
    [InlineData("invoice")]
    [InlineData("version")]
    [InlineData("binding")]
    public async Task ChangedImmutableAdmission_CannotReplayOwningReceipt(string change)
    {
        var row = await fixture.SeedAsync(true);
        await using var app = App(new LiveTransport());
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IQuotationInvoiceCompletionStore>();
        var authority = Context(row.Id, row.ModifiedDate!.Value);
        Assert.Equal(QuotationDecisionPersistenceStatus.Completed, (await store.ApplyAsync(authority, CancellationToken.None)).Status);
        var changed = change switch
        {
            "operation" => authority with { OperationId = Guid.NewGuid() },
            "employee" => authority with { EmployeeSubject = "employee-other" },
            "issuer" => authority with { OriginIssuer = "https://other.invalid" },
            "invoice" => authority with { InvoiceId = 902 },
            "version" => authority with { OriginalQuotationVersion = WireVersion(row.ModifiedDate.Value.AddSeconds(1)) },
            _ => authority with { FinancialBinding = new string('B', 64) },
        };
        Assert.Equal(QuotationDecisionPersistenceStatus.Conflict, (await store.ApplyAsync(changed, CancellationToken.None)).Status);
        Assert.Equal(authority.OperationId.ToString("D"), (await store.ReadAsync(authority.OperationId, CancellationToken.None))!.Receipt.OperationId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentAdmission_SameOperationConverges_DifferentOperationConflicts(bool sameOperation)
    {
        var row = await fixture.SeedAsync(null);
        await using var app = App(new LiveTransport());
        await using var firstScope = app.Services.CreateAsyncScope();
        await using var secondScope = app.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IQuotationInvoiceCompletionStore>();
        var second = secondScope.ServiceProvider.GetRequiredService<IQuotationInvoiceCompletionStore>();
        var authority = Context(row.Id, row.ModifiedDate!.Value);
        var other = sameOperation ? authority : authority with { OperationId = Guid.NewGuid() };
        var result = await Task.WhenAll(first.ApplyAsync(authority, CancellationToken.None), second.ApplyAsync(other, CancellationToken.None));
        Assert.Equal(sameOperation ? 2 : 1, result.Count(x => x.Status == QuotationDecisionPersistenceStatus.Completed));
        if (!sameOperation) Assert.Single(result, x => x.Status == QuotationDecisionPersistenceStatus.Conflict);
        await using var db = fixture.Context();
        Assert.Single(await db.InvoiceCompletionOperations.Where(x => x.QuotationId == row.Id).ToArrayAsync());
        Assert.Single(await db.AcceptedOutcomes.Where(x => x.QuotationId == row.Id).ToArrayAsync());
        Assert.Equal(901, (await db.Quotations.FindAsync(row.Id))!.InvoiceId);
    }

    [Theory]
    [InlineData("same")]
    [InlineData("operation")]
    [InlineData("binding")]
    public async Task AdmissionCommittedBetweenOperationAndQuotationReads_RequiresExactOwningReceipt(string change)
    {
        var row = await fixture.SeedAsync(null);
        var barrier = new AdmissionQuotationReadBarrier();
        await using var app = App(new LiveTransport()).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<Legacy.Maliev.QuotationService.Data.QuotationDbContext>(options => options.AddInterceptors(barrier))));
        await using var firstScope = app.Services.CreateAsyncScope();
        await using var secondScope = app.Services.CreateAsyncScope();
        var first = firstScope.ServiceProvider.GetRequiredService<IQuotationInvoiceCompletionStore>();
        var second = secondScope.ServiceProvider.GetRequiredService<IQuotationInvoiceCompletionStore>();
        var authority = Context(row.Id, row.ModifiedDate!.Value);
        var delayedAuthority = change switch
        {
            "operation" => authority with { OperationId = Guid.NewGuid() },
            "binding" => authority with { FinancialBinding = new string('B', 64) },
            _ => authority,
        };
        using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var delayed = first.ApplyAsync(delayedAuthority, lifetime.Token);
        QuotationDecisionPersistenceResult delayedResult;
        try
        {
            await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), lifetime.Token);
            Assert.Equal(QuotationDecisionPersistenceStatus.Completed,
                (await second.ApplyAsync(authority, lifetime.Token)).Status);
        }
        finally
        {
            barrier.Release();
            delayedResult = await delayed;
        }
        Assert.Equal(change == "same" ? QuotationDecisionPersistenceStatus.Completed : QuotationDecisionPersistenceStatus.Conflict,
            delayedResult.Status);
        await using var db = fixture.Context();
        var operation = Assert.Single(await db.InvoiceCompletionOperations.Where(x => x.QuotationId == row.Id).ToArrayAsync());
        Assert.Equal(authority.OperationId, operation.OperationId);
        Assert.Single(await db.AcceptedOutcomes.Where(x => x.QuotationId == row.Id).ToArrayAsync());
        Assert.Equal(901, (await db.Quotations.FindAsync(row.Id))!.InvoiceId);
        Assert.Equal(authority.FinancialBinding, (await second.ReadAsync(authority.OperationId, lifetime.Token))!.Receipt.FinancialBinding);
    }

    private sealed class AdmissionQuotationReadBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int firstRead;

        public void Release() => released.TrySetResult();

        public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData,
            InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal)
                && command.CommandText.Contains("FROM \"Quotation\"", StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref firstRead, 1, 0) == 0)
            {
                Entered.TrySetResult();
                await released.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
    }

    [Fact]
    public async Task LostCheckpointAcknowledgement_ReadbackProvesCompletionWithoutRepeatingOrderEffects()
    {
        var row = await fixture.SeedAsync(true);
        await LinkOrders(row.Id, 111, 112);
        var orders = new ReceiptOrders();
        await using var app = App(new LiveTransport(), orderClient: orders).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IQuotationInvoiceCompletionStore>();
                services.AddScoped<IQuotationInvoiceCompletionStore>(provider =>
                    new CheckpointAckLoss(provider.GetRequiredService<Legacy.Maliev.QuotationService.Data.QuotationRepository>()));
            }));
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        using var response = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var read = ReadOperation(row.Id, row.ModifiedDate.Value, operation);
        using var receiptResponse = await client.SendAsync(read);
        Assert.Equal("Completed", (await receiptResponse.Content.ReadFromJsonAsync<QuotationInvoiceCompletionReceipt>())!.State);
        using var replay = Decision(row.Id, row.ModifiedDate.Value, "bound", operation);
        using var completed = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(2, orders.Calls.Count);
    }

    [Theory]
    [InlineData("unbound", HttpStatusCode.Forbidden)]
    [InlineData("missing", HttpStatusCode.Forbidden)]
    [InlineData("other-version", HttpStatusCode.Forbidden)]
    [InlineData("other-invoice", HttpStatusCode.Forbidden)]
    [InlineData("wrong-route-operation", HttpStatusCode.Forbidden)]
    public async Task OwningReadback_RequiresCurrentExactBoundAuthority(string mode, HttpStatusCode status)
    {
        var row = await fixture.SeedAsync(true);
        await using var app = App(new LiveTransport());
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        using var accepted = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        var before = await Snapshot(row.Id);
        using var read = Decision(row.Id, row.ModifiedDate.Value, mode == "wrong-route-operation" ? "bound" : mode, operation);
        read.Method = HttpMethod.Get;
        read.RequestUri = new($"/quotations/{row.Id}/invoice-completion/operations/{(mode == "wrong-route-operation" ? Guid.NewGuid() : operation):D}?invoiceId=901", UriKind.Relative);
        read.Content?.Dispose();
        read.Content = null;
        using var response = await client.SendAsync(read);
        Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(before, await Snapshot(row.Id));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CapabilityWire_RequiresExactlyOneBearerPrefix_ForDecisionAndOwningGet(bool readback, bool doublePrefix)
    {
        var row = await fixture.SeedAsync(true);
        var before = await Snapshot(row.Id);
        await using var app = App(new LiveTransport());
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var request = readback ? ReadOperation(row.Id, row.ModifiedDate!.Value, operation)
            : Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        var header = Assert.Single(request.Headers.GetValues(QuotationInvoiceCapabilityVerifier.HeaderName));
        request.Headers.Remove(QuotationInvoiceCapabilityVerifier.HeaderName);
        request.Headers.Add(QuotationInvoiceCapabilityVerifier.HeaderName, doublePrefix ? "Bearer " + header : header[7..]);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, await Snapshot(row.Id));
        Assert.Empty(financialReads);
    }

    [Fact]
    public async Task OrderMembershipIsFrozenAtLinkAdmission_NewLinksDoNotExpandRecovery()
    {
        var row = await fixture.SeedAsync(true);
        await LinkOrders(row.Id, 121);
        var orders = new ReceiptOrders { FailOrder = 121 };
        await using var app = App(new LiveTransport(), orderClient: orders);
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        using var partial = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);
        await LinkOrders(row.Id, 122);
        orders.FailOrder = null;
        using var resume = Decision(row.Id, row.ModifiedDate.Value, "bound", operation);
        using var completed = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.DoesNotContain(orders.Calls, call => call.Order == 122);
        using var read = ReadOperation(row.Id, row.ModifiedDate.Value, operation);
        using var result = await client.SendAsync(read);
        var receipt = await result.Content.ReadFromJsonAsync<QuotationInvoiceCompletionReceipt>();
        Assert.Equal(1, receipt!.TotalOrders);
        Assert.Equal("Completed", receipt.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedReceiptReplay_RetainsResultAfterQuotationEditOrDeletion(bool delete)
    {
        var row = await fixture.SeedAsync(true);
        await LinkOrders(row.Id, 131);
        var orders = new ReceiptOrders();
        await using var app = App(new LiveTransport(), orderClient: orders);
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        using var firstResponse = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        var original = await firstResponse.Content.ReadAsStringAsync();
        await using (var db = fixture.Context())
        {
            await db.OrderLinks.Where(x => x.QuotationId == row.Id).ExecuteDeleteAsync();
            if (delete) await db.Quotations.Where(x => x.Id == row.Id).ExecuteDeleteAsync();
            else await db.Quotations.Where(x => x.Id == row.Id).ExecuteUpdateAsync(update =>
                update.SetProperty(x => x.Accepted, (bool?)false).SetProperty(x => x.InvoiceId, (int?)902)
                    .SetProperty(x => x.ModifiedDate, (DateTime?)row.ModifiedDate.Value.AddHours(1)));
        }
        using var replay = Decision(row.Id, row.ModifiedDate.Value, "bound", operation);
        using var replayResponse = await client.SendAsync(replay);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        Assert.Equal(original, await replayResponse.Content.ReadAsStringAsync());
        Assert.Single(orders.Calls);
        using var read = ReadOperation(row.Id, row.ModifiedDate.Value, operation);
        using var readResponse = await client.SendAsync(read);
        Assert.Equal("Completed", (await readResponse.Content.ReadFromJsonAsync<QuotationInvoiceCompletionReceipt>())!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialReceipt_LaterQuotationEditOrDeletion_FencesResumeWithoutOrderCalls(bool delete)
    {
        var row = await fixture.SeedAsync(true);
        await LinkOrders(row.Id, 141);
        var orders = new ReceiptOrders { FailOrder = 141 };
        await using var app = App(new LiveTransport(), orderClient: orders);
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        using var partial = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, partial.StatusCode);
        await using (var db = fixture.Context())
        {
            if (delete)
            {
                await db.OrderLinks.Where(x => x.QuotationId == row.Id).ExecuteDeleteAsync();
                await db.Quotations.Where(x => x.Id == row.Id).ExecuteDeleteAsync();
            }
            else await db.Quotations.Where(x => x.Id == row.Id).ExecuteUpdateAsync(update =>
                update.SetProperty(x => x.ModifiedDate, (DateTime?)row.ModifiedDate.Value.AddHours(1)));
        }
        orders.FailOrder = null;
        using var resume = Decision(row.Id, row.ModifiedDate.Value, "bound", operation);
        using var conflict = await client.SendAsync(resume);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Single(orders.Calls);
        using var read = ReadOperation(row.Id, row.ModifiedDate.Value, operation);
        using var response = await client.SendAsync(read);
        Assert.Equal("OrdersPartial", (await response.Content.ReadFromJsonAsync<QuotationInvoiceCompletionReceipt>())!.State);
    }

    [Theory]
    [InlineData("apply", false)]
    [InlineData("apply", true)]
    [InlineData("checkpoint", false)]
    [InlineData("checkpoint", true)]
    [InlineData("read", false)]
    [InlineData("read", true)]
    public async Task NormalHttpEfWriteOrRetryFailures_AreOpaque503_WithOwningRecovery(string phase, bool retryExhausted)
    {
        var row = await fixture.SeedAsync(true);
        await using var app = App(new LiveTransport()).WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IQuotationInvoiceCompletionStore>();
            services.AddScoped<IQuotationInvoiceCompletionStore>(provider => new EfFailureStore(
                provider.GetRequiredService<Legacy.Maliev.QuotationService.Data.QuotationRepository>(), phase, retryExhausted));
        }));
        using var client = fixture.Client(app);
        var operation = Guid.NewGuid();
        using var first = Decision(row.Id, row.ModifiedDate!.Value, "bound", operation);
        using var response = await client.SendAsync(first);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("synthetic-private-storage-details", await response.Content.ReadAsStringAsync());
        using var read = ReadOperation(row.Id, row.ModifiedDate.Value, operation);
        using var readResponse = await client.SendAsync(read);
        if (phase == "read") Assert.Equal(HttpStatusCode.ServiceUnavailable, readResponse.StatusCode);
        else if (phase == "apply") Assert.Equal(HttpStatusCode.NotFound, readResponse.StatusCode);
        else
        {
            Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
            Assert.Equal("Completed", (await readResponse.Content.ReadFromJsonAsync<QuotationInvoiceCompletionReceipt>())!.State);
        }
        Assert.DoesNotContain("synthetic-private-storage-details", await readResponse.Content.ReadAsStringAsync());
        Assert.True(readResponse.Headers.CacheControl?.NoStore);
    }

    private sealed class EfFailureStore(IQuotationInvoiceCompletionStore inner, string phase, bool retryExhausted) : IQuotationInvoiceCompletionStore
    {
        private Exception Failure => retryExhausted
            ? new Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException("synthetic-private-storage-details", new IOException())
            : new DbUpdateException("synthetic-private-storage-details", new IOException());
        public Task<QuotationDecisionPersistenceResult> ApplyAsync(QuotationInvoiceCompletionContext authority, CancellationToken cancellationToken)
            => phase == "apply" ? Task.FromException<QuotationDecisionPersistenceResult>(Failure) : inner.ApplyAsync(authority, cancellationToken);
        public Task<QuotationInvoiceCompletionProgress?> ReadAsync(Guid id, CancellationToken cancellationToken)
            => phase == "read" ? Task.FromException<QuotationInvoiceCompletionProgress?>(Failure) : inner.ReadAsync(id, cancellationToken);
        public Task<bool> ClaimAsync(Guid id, Guid claim, CancellationToken cancellationToken) => inner.ClaimAsync(id, claim, cancellationToken);
        public Task ReleaseAsync(Guid id, Guid claim, CancellationToken cancellationToken) => inner.ReleaseAsync(id, claim, cancellationToken);
        public async Task<bool> CheckpointAsync(Guid id, Guid claim, IReadOnlyList<int> completed, string state, CancellationToken cancellationToken)
        {
            var saved = await inner.CheckpointAsync(id, claim, completed, state, cancellationToken);
            if (phase == "checkpoint") throw Failure;
            return saved;
        }
    }

    private sealed class CheckpointAckLoss(IQuotationInvoiceCompletionStore inner) : IQuotationInvoiceCompletionStore
    {
        public Task<QuotationDecisionPersistenceResult> ApplyAsync(QuotationInvoiceCompletionContext authority, CancellationToken cancellationToken)
            => inner.ApplyAsync(authority, cancellationToken);
        public Task<QuotationInvoiceCompletionProgress?> ReadAsync(Guid id, CancellationToken cancellationToken) => inner.ReadAsync(id, cancellationToken);
        public Task<bool> ClaimAsync(Guid id, Guid claim, CancellationToken cancellationToken) => inner.ClaimAsync(id, claim, cancellationToken);
        public Task ReleaseAsync(Guid id, Guid claim, CancellationToken cancellationToken) => inner.ReleaseAsync(id, claim, cancellationToken);
        public async Task<bool> CheckpointAsync(Guid id, Guid claim, IReadOnlyList<int> completed, string state, CancellationToken cancellationToken)
        {
            var saved = await inner.CheckpointAsync(id, claim, completed, state, cancellationToken);
            if (saved && state == "Completed") throw new IOException("Synthetic acknowledgement loss after durable completion checkpoint.");
            return saved;
        }
    }

    private static QuotationInvoiceCompletionContext Context(int id, DateTime version) => new(Guid.NewGuid(), id, 901,
        "https://quotation95-auth.invalid", "employee-42", "service:legacy-intranet", "service:legacy-accounting",
        WireVersion(version), new string('A', 64), "invoice-creation-financial-v1");

    private static string WireVersion(DateTime version) => DateTime.SpecifyKind(version, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    private HttpRequestMessage ReadOperation(int id, DateTime version, Guid operation)
    {
        var request = Decision(id, version, "bound", operation);
        request.Method = HttpMethod.Get;
        request.RequestUri = new($"/quotations/{id}/invoice-completion/operations/{operation:D}?invoiceId=901", UriKind.Relative);
        request.Content?.Dispose();
        request.Content = null;
        return request;
    }

    private async Task LinkOrders(int id, params int[] orders)
    {
        await using var db = fixture.Context();
        db.OrderLinks.AddRange(orders.Select(order => new Legacy.Maliev.QuotationService.Domain.QuotationOrderLink { QuotationId = id, OrderId = order }));
        await db.SaveChangesAsync();
    }

    private sealed class LostAck : DelegatingHandler
    {
        private bool dropped;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (!dropped && request.Method == HttpMethod.Put)
            {
                dropped = true;
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                response.Dispose();
                throw new HttpRequestException("Synthetic acknowledgement loss after committed server response.");
            }
            return response;
        }
    }

    private sealed class ReceiptOrders : IOrderDecisionClient
    {
        internal readonly ConcurrentQueue<(int Order, string Key)> Calls = new();
        internal int? FailOrder { get; set; }
        internal OrderDecisionResult Failure { get; set; } = OrderDecisionResult.Unavailable;
        internal int? HoldOrder { get; set; }
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OrderDecisionResult> TransitionAsync(int orderId, bool accepted, string key, CancellationToken cancellationToken)
        {
            Assert.True(accepted);
            Calls.Enqueue((orderId, key));
            if (orderId == HoldOrder)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                finally { Cancelled.TrySetResult(); }
            }
            return orderId == FailOrder ? Failure : OrderDecisionResult.Completed;
        }
    }

    private WebApplicationFactory<Program> App(LiveTransport transport, TimeProvider? clock = null, string financialMode = "bound", IOrderDecisionClient? orderClient = null) => fixture.App(new())
        .WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MALIEV_OBSERVABILITY_STANDBY", "true");
            builder.UseSetting("QuotationInvoiceCompletion:Enabled", "true");
            builder.UseSetting("Features:ResourceScopedAuthEnabled", "true");
            builder.UseSetting("Features:AllowExactServiceClaimsForLiveCheck", "false");
            builder.UseSetting("Features:FailOpenOnIAMError", "false");
            builder.UseSetting("Services:Accounting:BaseUrl", "https://quotation95-accounting.invalid");
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient("IAMService").ConfigurePrimaryHttpMessageHandler(() => transport);
                services.AddHttpClient<QuotationInvoiceFinancialAuthorityClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => new FinancialTransport(financialOperations, financialReads, financialMode));
                if (clock is not null) services.AddSingleton(clock);
                if (orderClient is not null)
                {
                    services.RemoveAll<IOrderDecisionClient>();
                    services.AddSingleton(orderClient);
                }
            });
        });

    private HttpRequestMessage Decision(int quotationId, DateTime version, string mode, Guid? operationId = null, TimeProvider? proofClock = null)
    {
        var operation = operationId ?? Guid.NewGuid();
        var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{quotationId}/decision")
        {
            Content = JsonContent.Create(new { Accepted = true, EmployeeInitiated = true, InvoiceId = 901 }),
        };
        request.Headers.Add("Idempotency-Key", operation.ToString("D"));
        request.Headers.Add("X-Expected-Modified-Date", DateTime.SpecifyKind(version, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
        if (mode != "missing") request.Headers.Add(QuotationInvoiceCapabilityVerifier.HeaderName,
            "Bearer " + fixture.InvoiceCapability(quotationId, 901, operation, version, mode, proofClock));
        financialOperations[operation] = new()
        {
            ["ContractVersion"] = 1,
            ["OperationId"] = operation.ToString("D"),
            ["QuotationId"] = quotationId,
            ["InvoiceId"] = 901,
            ["OriginIssuer"] = "https://quotation95-auth.invalid",
            ["EmployeeSubject"] = "employee-42",
            ["RequesterSubject"] = "service:legacy-intranet",
            ["OriginalQuotationVersion"] = DateTime.SpecifyKind(version, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture),
            ["FinancialBinding"] = new string('A', 64),
        };
        return request;
    }

    private async Task<string> Snapshot(int id)
    {
        await using var db = fixture.Context();
        return JsonSerializer.Serialize(new
        {
            Quotation = await db.Quotations.AsNoTracking().SingleAsync(value => value.Id == id),
            Outcomes = await db.AcceptedOutcomes.AsNoTracking().Where(value => value.QuotationId == id).ToArrayAsync(),
            Analytics = await db.GoogleAnalyticsOutbox.AsNoTracking().Where(value => value.QuotationId == id).ToArrayAsync(),
            Operations = await db.InvoiceCompletionOperations.AsNoTracking().Where(value => value.QuotationId == id).ToArrayAsync(),
        });
    }

    private static async Task DrainPendingAsync(Task<HttpResponseMessage> pending, CancellationTokenSource caller)
    {
        caller.Cancel();
        try { using var abandoned = await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (TimeoutException)
        {
            // Keep final observation/disposal attached to the finite request, without masking a primary failure.
            _ = pending.ContinueWith(completed =>
            {
                if (completed.IsCompletedSuccessfully) completed.Result.Dispose();
                else _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception) { _ = pending.Exception; }
    }

    private static void AssertWire(LiveTransport transport, int id, bool employeeExpected)
    {
        var records = transport.Records.ToArray();
        Assert.Equal(employeeExpected ? 2 : 1, records.Length);
        Assert.Equal("service:legacy-accounting", records[0].Body.GetProperty("principalId").GetString());
        if (employeeExpected) Assert.Equal("employee-42", records[1].Body.GetProperty("principalId").GetString());
        Assert.All(records, record =>
        {
            Assert.Equal(HttpMethod.Post, record.Method);
            Assert.Equal("/iam/v1/auth/check-permission", record.Path);
            Assert.Equal("service:legacy-quotation", record.Workload);
            Assert.True(record.CredentialPresent);
            Assert.Equal(4, record.Body.EnumerateObject().Count());
            Assert.Equal(QuotationPermissions.QuotationsUpdate, record.Body.GetProperty("permissionId").GetString());
            Assert.Equal($"/quotations/{id}", record.Body.GetProperty("resourcePath").GetString());
            Assert.True(record.Body.GetProperty("bypassCache").GetBoolean());
        });
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        internal void Advance(TimeSpan delta) => now += delta;
    }

    private sealed record Wire(HttpMethod Method, string Path, string? Workload, bool CredentialPresent, JsonElement Body);
    private sealed class FinancialTransport(ConcurrentDictionary<Guid, Dictionary<string, object>> operations,
        ConcurrentQueue<(HttpMethod Method, string Path, string? Workload)> records, string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jwt = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(
                request.Headers.Authorization?.Parameter ?? throw new InvalidOperationException("Missing synthetic workload authentication."));
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing synthetic financial path.");
            records.Enqueue((request.Method, uri.AbsolutePath, jwt.Subject));
            var operation = Guid.Parse(uri.AbsolutePath.Split('/')[4]);
            var receipt = new Dictionary<string, object>(operations[operation]);
            if (mode == "unavailable") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (mode == "transport") throw new HttpRequestException("Synthetic owning financial transport failure.");
            if (mode == "drift") receipt["FinancialBinding"] = new string('B', 64);
            if (mode == "extra") receipt["Unexpected"] = true;
            if (mode == "type") receipt["InvoiceId"] = "901";
            var json = JsonSerializer.Serialize(receipt);
            if (mode == "duplicate") json = json.Replace("\"ContractVersion\":1", "\"ContractVersion\":1,\"ContractVersion\":1", StringComparison.Ordinal);
            if (mode == "oversized") json += new string(' ', 4097);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class LiveTransport : HttpMessageHandler
    {
        internal bool DenyEmployee { get; init; }
        internal bool WaitForEmployeeCancellation { get; init; }
        internal bool HoldEmployeeResponse { get; init; }
        internal ConcurrentQueue<Wire> Records { get; } = new();
        internal TaskCompletionSource EmployeeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource EmployeeCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource EmployeeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(
                request.Headers.Authorization?.Parameter ?? throw new InvalidOperationException("Missing synthetic workload authentication."));
            var body = json.RootElement.Clone();
            Records.Enqueue(new(request.Method, request.RequestUri!.AbsolutePath, token.Subject,
                request.Headers.TryGetValues("X-Maliev-IAM-Live-Check-Key", out var keys) && keys.Count() == 1 && !string.IsNullOrWhiteSpace(keys.Single()), body));
            var employee = body.GetProperty("principalId").GetString() == "employee-42";
            if (employee)
            {
                EmployeeEntered.TrySetResult();
                if (HoldEmployeeResponse) await EmployeeRelease.Task.WaitAsync(cancellationToken);
                if (WaitForEmployeeCancellation)
                {
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                    finally { EmployeeCancelled.TrySetResult(); }
                }
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { allowed = !employee || !DenyEmployee }) };
        }
    }
}
