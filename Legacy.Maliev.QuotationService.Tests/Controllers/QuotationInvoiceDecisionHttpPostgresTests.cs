using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Api.Clients;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Services;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>
/// Quotation68 producer regressions: real HTTP binding, controller, workflow and PostgreSQL.
/// Test-owned RS256 keys use the normal JWT validator; permission policy remains synthetic, not live IAM.
/// </summary>
public sealed class QuotationInvoiceDecisionHttpPostgresTests(QuotationInvoiceDecisionPostgresFixture database)
    : IClassFixture<QuotationInvoiceDecisionPostgresFixture>
{
    private static readonly DateTime SeedTime = new(2026, 9, 29, 3, 4, 5, DateTimeKind.Unspecified);
    private static readonly JsonSerializerOptions SourceRequestJson = new() { PropertyNamingPolicy = null };

    [Theory]
    [InlineData("123.456", null, null, "THB")]
    [InlineData(null, "789", null, "THB")]
    [InlineData(" ", "789", null, "THB")]
    [InlineData("123.456", "789", "private@example.test", "THB")]
    [InlineData("123.456", "789", null, "123")]
    [InlineData("123.456", "789", null, null)]
    [InlineData("123.456", "789", null, "TH")]
    [MemberData(nameof(OversizedAnalyticsContexts))]
    public async Task Decision_InvalidAnalyticsHttpContext_DoesNotWriteFinancialOrDeliveryRows(
        string? clientId, string? sessionId, string? userId, string? currency)
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{seeded.Id}/decision")
        {
            Content = JsonContent.Create(new
            {
                Accepted = true,
                EmployeeInitiated = true,
                InvoiceId = 901,
                ClientId = clientId,
                SessionId = sessionId,
                UserId = userId,
                Currency = currency
            }, options: SourceRequestJson),
        };
        Authenticate(request);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUnchangedAsync(seeded, 0);
        Assert.Empty(await DeliveryRowsAsync(seeded.Id));
    }

    public static TheoryData<string?, string?, string?, string?> OversizedAnalyticsContexts => new()
    {
        { new string('1', 129), "789", null, "THB" },
        { "123.456", new string('1', 129), null, "THB" },
        { "123.456", "789", new string('u', 129), "THB" },
    };

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", " ")]
    public async Task Decision_NoAnalyticsIdPair_OptionalUserAndCurrencyDoNotPreventNeutralAcceptance(
        string? clientId, string? sessionId)
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{seeded.Id}/decision")
        {
            Content = JsonContent.Create(new
            {
                Accepted = true,
                EmployeeInitiated = true,
                InvoiceId = 901,
                ClientId = clientId,
                SessionId = sessionId,
                UserId = "private@example.test",
                Currency = "123"
            },
                options: SourceRequestJson),
        };
        Authenticate(request);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = database.QuotationContext();
        var stored = await read.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.True(stored.Accepted);
        Assert.Equal(901, stored.InvoiceId);
        Assert.Single(await read.AcceptedOutcomes.Where(x => x.QuotationId == seeded.Id).ToListAsync());
        Assert.Empty(await DeliveryRowsAsync(seeded.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_ConsentedFirstIntent_IsAtomicAndNeverDuplicatedByReplay(bool concurrent)
    {
        var seeded = await SeedAsync();
        var occurred = new DateTimeOffset(2026, 9, 30, 4, 5, 6, TimeSpan.Zero).AddTicks(7);
        await using var app = await StartAppAsync(concurrent ? new TwoDecisionSaveBarrier() : null, clock: occurred);
        using var client = app.GetTestClient();
        var responses = concurrent
            ? await Task.WhenAll(DecideAsync(client, seeded.Id, 901, consented: true), DecideAsync(client, seeded.Id, 901, consented: true))
            : [await DecideAsync(client, seeded.Id, 901, consented: true)];
        try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }
        using var replay = await DecideAsync(client, seeded.Id, 901, consented: true);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var read = database.QuotationContext();
        var stored = await read.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        var neutral = Assert.Single(await read.AcceptedOutcomes.Where(x => x.QuotationId == seeded.Id).ToListAsync());
        // The business decision, invoice and neutral fact must succeed before checking the missing delivery behavior.
        Assert.True(stored.Accepted);
        Assert.Equal(901, stored.InvoiceId);
        var delivery = Assert.Single(await DeliveryRowsAsync(seeded.Id));
        Assert.Equal(neutral.AcceptedUtc.AddTicks(neutral.AcceptedUtcSubMicrosecondTicks), delivery.OccurredUtc);
        Assert.Equal(stored.AcceptedUtc, delivery.OccurredUtc);
        Assert.Equal(seeded.SourceRequestId, delivery.SourceRequestId);
        Assert.Equal(seeded.SourceJourneyId, delivery.SourceJourneyId);
        Assert.Equal("quotation-" + seeded.Id + ":close_convert_lead:v1", delivery.EventKey);
        Assert.Equal("close_convert_lead", delivery.EventName);
        Assert.Equal("123.456", delivery.ClientId);
        Assert.Equal("789", delivery.SessionId);
        Assert.Equal("opaque-7", delivery.UserId);
        Assert.Equal("THB", delivery.Currency);
        Assert.Equal(104m, delivery.Value);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Null(delivery.SentUtc);
    }

    // Service identity is synthetic here; real Accounting delegation/live authorization requires the joined lane.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_AccountingServicePositiveInvoice_PreservesCustomerOriginAndOptionalContext(bool consented)
    {
        var seeded = await SeedAsync();
        var orders = new FixtureOrderHandler();
        await using var app = await StartAppAsync(orders: orders);
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 901, employeeInitiated: false,
            profile: "accounting", consented: consented);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = database.QuotationContext();
        var quotation = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        var neutral = Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
        Assert.True(quotation.Accepted);
        Assert.Equal(901, quotation.InvoiceId);
        Assert.Equal("customer", quotation.AcceptanceOrigin);
        Assert.Equal("customer", neutral.AcceptanceOrigin);
        Assert.Single(orders.Requests);
        var delivery = await DeliveryRowsAsync(seeded.Id);
        if (consented)
        {
            var captured = Assert.Single(delivery);
            Assert.Equal("123.456", captured.ClientId);
            Assert.Equal("789", captured.SessionId);
            Assert.Equal(quotation.AcceptedUtc, captured.OccurredUtc);
        }
        else Assert.Empty(delivery);
    }

    // Characterizes the private candidate's migrated null/zero-invoice annex; not source acceptance parity.
    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData(0, true)]
    public async Task Decision_ConsentedNonPositiveInvoice_CharacterizesFirstCaptureBeforeLateAttachment(
        int? invoiceId, bool employeeInitiated)
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var first = await DecideAsync(client, seeded.Id, invoiceId,
            employeeInitiated: employeeInitiated, consented: true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var original = Assert.Single(await DeliveryRowsAsync(seeded.Id));
        await using (var read = database.QuotationContext())
        {
            var quotation = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
            Assert.True(quotation.Accepted);
            Assert.Null(quotation.InvoiceId);
            Assert.Equal(employeeInitiated ? "employee" : "customer", quotation.AcceptanceOrigin);
            Assert.Equal(quotation.AcceptedUtc, original.OccurredUtc);
            Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
        }

        using var attachment = await DecideAsync(client, seeded.Id, 901, consented: false);
        Assert.Equal(HttpStatusCode.OK, attachment.StatusCode);
        Assert.Equal(original, Assert.Single(await DeliveryRowsAsync(seeded.Id)));
        await using var attached = database.QuotationContext();
        var stored = await attached.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal(901, stored.InvoiceId);
        Assert.Equal(original.OccurredUtc, stored.AcceptedUtc);
        Assert.Single(await attached.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
    }

    [Fact]
    public async Task Decision_ConsentedFirstIntent_ReplayCannotReplaceContextOrInvoice()
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var first = await DecideAsync(client, seeded.Id, 901, consented: true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var before = Assert.Single(await DeliveryRowsAsync(seeded.Id));
        foreach (var invoice in new[] { 901, 902 })
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{seeded.Id}/decision")
            {
                Content = JsonContent.Create(new
                {
                    Accepted = true,
                    EmployeeInitiated = true,
                    InvoiceId = invoice,
                    ClientId = "999.888",
                    SessionId = "777",
                    UserId = "opaque-other",
                    Currency = "USD"
                }, options: SourceRequestJson),
            };
            Authenticate(request);
            using var response = await client.SendAsync(request);
            Assert.Equal(invoice == 901 ? HttpStatusCode.OK : HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(before, Assert.Single(await DeliveryRowsAsync(seeded.Id)));
        }
        await using var read = database.QuotationContext();
        var stored = await read.Quotations.AsNoTracking().SingleAsync(x => x.Id == seeded.Id);
        Assert.Equal(901, stored.InvoiceId);
        Assert.Equal(before.OccurredUtc, stored.AcceptedUtc);
        Assert.Single(await read.AcceptedOutcomes.Where(x => x.QuotationId == seeded.Id).ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_NoFirstConsent_ReplayAndLateInvoiceNeverCreateDelivery(bool historical)
    {
        var seeded = await SeedAsync(accepted: historical ? true : null);
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var first = await DecideAsync(client, seeded.Id, 0);
        using var replay = await DecideAsync(client, seeded.Id, 0, consented: true);
        using var attachment = await DecideAsync(client, seeded.Id, 901, consented: true);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, attachment.StatusCode);
        await using var read = database.QuotationContext();
        Assert.Empty(await DeliveryRowsAsync(seeded.Id));
        Assert.Single(await read.AcceptedOutcomes.Where(x => x.QuotationId == seeded.Id).ToListAsync());
    }

    [Fact]
    public async Task Decision_DeliveryInsertDatabaseFault_RollsBackInvoiceDecisionAndNeutralFact()
    {
        var seeded = await SeedAsync();
        Assert.True(await AnalyticsTableExistsAsync(), "Accepted runtime has no durable analytics delivery table; rollback acceptance cannot run until the intent slice exists.");
        await using var connection = new NpgsqlConnection(database.QuotationConnectionString);
        await connection.OpenAsync();
        // Disposable fixture only: inject a real PostgreSQL constraint failure after decision mutation.
        var constraint = $"quotation101_reject_{seeded.Id}";
        await using (var command = new NpgsqlCommand($"ALTER TABLE \"GoogleAnalyticsOutbox\" ADD CONSTRAINT \"{constraint}\" CHECK (\"QuotationID\" <> {seeded.Id})", connection))
            await command.ExecuteNonQueryAsync();
        try
        {
            await using var app = await StartAppAsync();
            using var client = app.GetTestClient();
            using var failure = await DecideAsync(client, seeded.Id, 901, consented: true);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failure.StatusCode);
            await AssertUnchangedAsync(seeded, 0);
            Assert.Empty(await DeliveryRowsAsync(seeded.Id));
        }
        finally
        {
            await using var command = new NpgsqlCommand($"ALTER TABLE \"GoogleAnalyticsOutbox\" DROP CONSTRAINT \"{constraint}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_HistoricalMissingAcceptanceMetadata_LateIntentReplayNeverAdoptsOutcome(bool concurrent)
    {
        var seeded = await SeedAsync(accepted: true);
        await using (var context = database.QuotationContext())
        {
            await context.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ExecuteDeleteAsync();
            var row = await context.Quotations.FindAsync(seeded.Id);
            row!.AcceptedUtc = null;
            row.AcceptanceOrigin = null;
            await context.SaveChangesAsync();
        }
        await using var app = await StartAppAsync(concurrent ? new TwoDecisionSaveBarrier() : null);
        using var client = app.GetTestClient();
        var responses = concurrent
            ? await Task.WhenAll(DecideAsync(client, seeded.Id, 901), DecideAsync(client, seeded.Id, 901))
            : [await DecideAsync(client, seeded.Id, 901)];
        try { Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode)); }
        finally { foreach (var response in responses) response.Dispose(); }
        using var replay = await DecideAsync(client, seeded.Id, 901);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var read = database.QuotationContext();
        var stored = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal(901, stored.InvoiceId);
        Assert.True(stored.Accepted);
        Assert.Null(stored.AcceptedUtc);
        Assert.Null(stored.AcceptanceOrigin);
        Assert.Equal(seeded.SourceRequestId, stored.SourceRequestId);
        Assert.Equal(seeded.SourceJourneyId, stored.SourceJourneyId);
        Assert.Empty(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
    }

    [Fact]
    public async Task Decision_LateAttachmentThenDecline_StartsNewStableOrderTransitionVersion()
    {
        var seeded = await SeedAsync(accepted: true);
        var orders = new FixtureOrderHandler();
        await using var app = await StartAppAsync(orders: orders);
        using var client = app.GetTestClient();
        using var attachment = await DecideAsync(client, seeded.Id, 901);
        using var decline = await DecideAsync(client, seeded.Id, null, accepted: false);
        using var replay = await DecideAsync(client, seeded.Id, null, accepted: false);
        Assert.Equal(HttpStatusCode.OK, attachment.StatusCode);
        Assert.Equal(HttpStatusCode.OK, decline.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var transitions = orders.Requests.ToArray();
        Assert.Equal(3, transitions.Length);
        Assert.NotEqual(transitions[0].Key, transitions[1].Key);
        Assert.Equal(transitions[1].Key, transitions[2].Key);
        await using var read = database.QuotationContext();
        var stored = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.False(stored.Accepted);
        Assert.Null(stored.DecisionOrderVersion);
        Assert.Equal(901, stored.InvoiceId);
        Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
    }

    [Fact]
    public async Task DecisionSchema_PhysicalNullableColumnAndMigrationHistoryArePresent()
    {
        await using var context = database.QuotationContext();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Contains("20260930070000_PreserveDecisionOrderVersion", await context.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema='public' AND table_name='Quotation' AND column_name='DecisionOrderVersion' AND data_type='timestamp without time zone' AND is_nullable='YES'").SingleAsync());
    }

    [Fact]
    public async Task DecisionSchema_PreviousPhysicalSchemaFailsClosed_ThenAdditiveUpgradePreservesRows()
    {
        await using (var connection = new NpgsqlConnection(database.QuotationConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE quotation68_readiness", connection);
            await command.ExecuteNonQueryAsync();
        }
        var connectionString = new NpgsqlConnectionStringBuilder(database.QuotationConnectionString) { Database = "quotation68_readiness" }.ConnectionString;
        await using var context = new QuotationDbContext(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(connectionString).Options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync("20260927050000_PreserveHistoricalAcceptedUtc");
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Quotation" ("Period", "ExpirationDate", "Subtotal", "Vat", "Total", "CurrencyID", "Accepted", "InvoiceID", "ModifiedDate")
            VALUES (30, TIMESTAMP '2026-12-31', 100, 7, 107, 764, TRUE, 901, TIMESTAMP '2026-09-29 04:04:05');
            """);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => context.Quotations.AsNoTracking().ToListAsync());
        Assert.Equal(PostgresErrorCodes.UndefinedColumn, exception.SqlState);
        await migrator.MigrateAsync();
        var row = Assert.Single(await context.Quotations.AsNoTracking().ToListAsync());
        Assert.True(row.Accepted);
        Assert.Equal(901, row.InvoiceId);
        Assert.Null(row.DecisionOrderVersion);
        Assert.Equal(SeedTime.AddHours(1), row.ModifiedDate);
        await migrator.MigrateAsync();
        Assert.Single(await context.Quotations.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(0, true)]
    [InlineData(901, false)]
    public async Task Decision_ValidInitialIntent_AcceptsAndPersistsOnlyPositiveInvoice(int? invoiceId, bool employeeInitiated)
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, invoiceId, employeeInitiated: employeeInitiated);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = database.QuotationContext();
        var row = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.True(row.Accepted);
        Assert.Equal(invoiceId > 0 ? invoiceId : null, row.InvoiceId);
        Assert.Equal(employeeInitiated ? "employee" : "customer", row.AcceptanceOrigin);
        Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
    }

    [Fact]
    public async Task Decision_LateAttachmentDatabaseFault_RollsBackInvoiceVersionBindingAndModifiedDate()
    {
        var seeded = await SeedAsync(accepted: true);
        await using (var connection = new NpgsqlConnection(database.QuotationConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                $"ALTER TABLE \"Quotation\" ADD CONSTRAINT \"quotation68_reject_late_invoice\" CHECK (\"ID\" <> {seeded.Id.ToString(CultureInfo.InvariantCulture)} OR \"InvoiceID\" IS NULL)", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 901);
        Assert.False(response.IsSuccessStatusCode);
        await AssertUnchangedAsync(seeded, 1);
        await using var read = database.QuotationContext();
        Assert.Null((await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id)).DecisionOrderVersion);
    }

    [Fact]
    public async Task Decision_HistoricalPartialSaga_LateAttachmentAndReplayKeepOriginalOrderKey()
    {
        var seeded = await SeedAsync(accepted: true);
        await using (var context = database.QuotationContext())
        {
            var row = await context.Quotations.FindAsync(seeded.Id);
            row!.ModifiedDate = SeedTime.AddHours(1);
            await context.SaveChangesAsync();
        }
        var orders = new FixtureOrderHandler(unavailableRequests: 1);
        await using var app = await StartAppAsync(orders: orders);
        using var client = app.GetTestClient();
        using var original = await DecideAsync(client, seeded.Id, 0);
        using var attachment = await DecideAsync(client, seeded.Id, 901);
        using var replay = await DecideAsync(client, seeded.Id, 901);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, original.StatusCode);
        Assert.Equal(HttpStatusCode.OK, attachment.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var requests = orders.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        var originalVersion = DateTime.SpecifyKind(SeedTime.AddHours(1), DateTimeKind.Utc);
        Assert.All(requests, request => Assert.Equal($"quotation-{seeded.Id}-accepted-{originalVersion.Ticks:x}-order-701", request.Key));
    }

    [Theory]
    [InlineData("accounting")]
    [InlineData("wildcard")]
    [InlineData("intranet-wildcard")]
    [InlineData("intranet-singular")]
    [InlineData("employee-service-kind")]
    [InlineData("employee-role-only")]
    [InlineData("employee-duplicate-sub")]
    [InlineData("employee-duplicate-kind")]
    [InlineData("employee-conflicting-alias")]
    [InlineData("employee-service-prefix")]
    [InlineData("employee-customer-kind")]
    public async Task Decision_UntrustedEmployeeOverride_IsForbidden(string profile)
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 901, profile: profile);
        Assert.Equal(profile == "employee-duplicate-sub" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUnchangedAsync(seeded, 0);
    }

    [Fact]
    public async Task Decision_CurrentEmployeeShapeWithoutRole_PreservesEmployeeOrigin()
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 0, profile: "employee-no-role");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = database.QuotationContext();
        Assert.Equal("employee", (await read.Quotations.SingleAsync(value => value.Id == seeded.Id)).AcceptanceOrigin);
        Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
    }

    [Fact]
    public async Task Decision_TrustedIntranetService_CanAttachPositiveInvoice()
    {
        var seeded = await SeedAsync(accepted: true);
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 901, profile: "intranet");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = database.QuotationContext();
        Assert.Equal(901, (await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id)).InvoiceId);
    }

    [Theory]
    [InlineData(-1, true, true)]
    [InlineData(901, false, true)]
    [InlineData(0, true, false)]
    public async Task Decision_InvalidInvoiceIntent_IsBadRequestWithoutMutation(int invoiceId, bool accepted, bool employeeInitiated)
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, invoiceId, accepted, employeeInitiated);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUnchangedAsync(seeded, 0);
    }

    [Fact]
    public async Task Decision_EmployeeZeroIntent_DoesNotClearStoredInvoice()
    {
        var seeded = await SeedAsync(accepted: true, invoiceId: 901);
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 0);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertUnchangedAsync(seeded, 1);
    }

    [Fact]
    public async Task Decision_HistoricalZeroInvoice_CannotBeReplaced()
    {
        var seeded = await SeedAsync(accepted: true, invoiceId: 0);
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 901);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(seeded, 1);
    }

    [Fact]
    public async Task Decision_NonEmployeeLateInvoice_ConflictsWithoutMutation()
    {
        var seeded = await SeedAsync(accepted: true);
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 901, employeeInitiated: false);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(seeded, 1);
    }

    [Fact]
    public async Task Decision_StaleExpectedVersion_LateAttachmentConflicts()
    {
        var seeded = await SeedAsync(accepted: true);
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 901, expected: SeedTime.AddSeconds(-1));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(seeded, 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task Decision_ConcurrentSameInvoiceIntents_BothReplayOnePersistedLink(bool? accepted)
    {
        var seeded = await SeedAsync(accepted: accepted);
        await using var app = await StartAppAsync(new TwoDecisionSaveBarrier());
        using var client = app.GetTestClient();
        var responses = await Task.WhenAll(DecideAsync(client, seeded.Id, 901), DecideAsync(client, seeded.Id, 901));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            await using var read = database.QuotationContext();
            var stored = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
            Assert.Equal(901, stored.InvoiceId);
            Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Decision_PositiveInvoiceIntent_PersistsInvoiceWithFirstAcceptance()
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var response = await DecideAsync(client, seeded.Id, 901);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = database.QuotationContext();
        var stored = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal(901, stored.InvoiceId);
        Assert.True(stored.Accepted);
        var outcome = Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
        Assert.Equal($"quotation-{seeded.Id}:accepted:v1", outcome.EventKey);
        Assert.Equal("employee", outcome.AcceptanceOrigin);
        Assert.Equal(seeded.SourceRequestId, outcome.SourceRequestId);
        Assert.Equal(seeded.SourceJourneyId, outcome.SourceJourneyId);
    }

    [Fact]
    public async Task Decision_DifferentLinkedInvoice_ConflictsWithoutChangingFirstAcceptance()
    {
        var seeded = await SeedAsync(accepted: true, invoiceId: 901);
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var response = await DecideAsync(client, seeded.Id, 902);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(seeded, outcomeCount: 1);
    }

    [Fact]
    public async Task Decision_LateInvoiceAttachment_PreservesFirstOutcomeAndAcceptedTime()
    {
        var seeded = await SeedAsync(accepted: true);
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var response = await DecideAsync(client, seeded.Id, 901);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = database.QuotationContext();
        var stored = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal(901, stored.InvoiceId);
        Assert.Equal(SeedTime, stored.AcceptedUtc);
        Assert.Equal("employee", stored.AcceptanceOrigin);
        var outcome = Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
        Assert.Equal(SeedTime, outcome.AcceptedUtc);
        Assert.Equal((short)0, outcome.AcceptedUtcSubMicrosecondTicks);
    }

    [Fact]
    public async Task Decision_SameInvoiceReplay_PreservesTimestampAndSingleOutcome()
    {
        var seeded = await SeedAsync(accepted: true, invoiceId: 901);
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var first = await DecideAsync(client, seeded.Id, 901);
        using var replay = await DecideAsync(client, seeded.Id, 901);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await AssertUnchangedAsync(seeded, outcomeCount: 1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task Decision_ConcurrentDifferentInvoiceIntents_OnlyOneCompletes(bool? accepted)
    {
        var seeded = await SeedAsync(accepted: accepted);
        var barrier = new TwoDecisionSaveBarrier();
        await using var app = await StartAppAsync(barrier);
        using var client = app.GetTestClient();

        var responses = await Task.WhenAll(DecideAsync(client, seeded.Id, 901), DecideAsync(client, seeded.Id, 902));
        try
        {
            Assert.Equal(1, responses.Count(value => value.StatusCode == HttpStatusCode.OK));
            Assert.Equal(1, responses.Count(value => value.StatusCode == HttpStatusCode.Conflict));
            await using var read = database.QuotationContext();
            var stored = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
            Assert.Contains(stored.InvoiceId, new int?[] { 901, 902 });
            Assert.True(stored.Accepted);
            Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Fact]
    public async Task Decision_DatabaseRejectsOutcome_RollsBackQuotationAndInvoiceIntent()
    {
        var seeded = await SeedAsync();
        await using (var connection = new NpgsqlConnection(database.QuotationConnectionString))
        {
            await connection.OpenAsync();
            // The integer comes only from this disposable database's generated row, never request text.
            await using var command = new NpgsqlCommand(
                $"ALTER TABLE \"QuotationAcceptedOutcome\" ADD CONSTRAINT \"quotation68_reject_outcome\" CHECK (\"QuotationID\" <> {seeded.Id.ToString(CultureInfo.InvariantCulture)})",
                connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();

        using var response = await DecideAsync(client, seeded.Id, 901);

        Assert.False(response.IsSuccessStatusCode);
        await AssertUnchangedAsync(seeded, outcomeCount: 0);
    }

    [Fact]
    public async Task GenericUpdate_AccountingOldPromotionIntent_IsRejectedWithoutPersistingInvoice()
    {
        var seeded = await SeedAsync();
        var orders = new FixtureOrderHandler();
        await using var app = await StartAppAsync(orders: orders);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{seeded.Id}")
        {
            Content = JsonContent.Create(new
            {
                seeded.CustomerId,
                seeded.EmployeeId,
                InvoiceId = 901,
                seeded.Period,
                seeded.ExpirationDate,
                seeded.Subtotal,
                seeded.Vat,
                seeded.Total,
                seeded.WithholdingTax,
                seeded.CurrencyId,
                seeded.Comment,
                seeded.Fob,
                seeded.ShippedVia,
                seeded.Terms,
                Accepted = true,
            }),
        };
        Authenticate(request);
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        await AssertUnchangedAsync(seeded, outcomeCount: 0);
        Assert.Empty(await DeliveryRowsAsync(seeded.Id));
        Assert.Empty(orders.Requests);
    }

    [Fact]
    public async Task Decision_OmittedInvoiceIntent_PreservesExistingClientAndOrderTransitionContract()
    {
        var seeded = await SeedAsync();
        var orders = new FixtureOrderHandler();
        await using var app = await StartAppAsync(orders: orders);
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{seeded.Id}/decision")
        {
            Content = JsonContent.Create(new { Accepted = true, EmployeeInitiated = true }),
        };
        Authenticate(request);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await using var read = database.QuotationContext();
        var stored = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.Null(stored.InvoiceId);
        Assert.True(stored.Accepted);
        Assert.Single(await read.AcceptedOutcomes.Where(value => value.QuotationId == seeded.Id).ToListAsync());
        var transition = Assert.Single(orders.Requests);
        Assert.Equal("/orderstatuses/histories/701/accepted", transition.Path);
        Assert.StartsWith($"quotation-{seeded.Id}-accepted-", transition.Key, StringComparison.Ordinal);
    }

    private async Task<bool> AnalyticsTableExistsAsync()
    {
        await using var connection = new NpgsqlConnection(database.QuotationConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass('public.\"GoogleAnalyticsOutbox\"') IS NOT NULL", connection);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyList<AnalyticsDeliverySnapshot>> DeliveryRowsAsync(int quotationId)
    {
        // Absence means no persisted delivery intent, rather than an undefined-table fixture failure.
        if (!await AnalyticsTableExistsAsync()) return [];
        await using var connection = new NpgsqlConnection(database.QuotationConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "SourceRequestID", "SourceJourneyID", "EventKey", "EventName", "ClientId", "SessionId",
                   "UserId", "Currency", "Value", "OccurredUtc", "AttemptCount", "SentUtc"
            FROM "GoogleAnalyticsOutbox" WHERE "QuotationID" = @quotationId
            """, connection);
        command.Parameters.AddWithValue("quotationId", quotationId);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AnalyticsDeliverySnapshot>();
        while (await reader.ReadAsync())
            rows.Add(new(reader.IsDBNull(0) ? null : reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetGuid(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6), reader.GetString(7), reader.GetDecimal(8),
                ReadExactDate(reader.GetValue(9)), reader.GetInt32(10), reader.IsDBNull(11) ? null : ReadExactDate(reader.GetValue(11))));
        return rows;
    }

    private static DateTime ReadExactDate(object value) => value is DateTime date ? date
        : DateTime.ParseExact((string)value, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private sealed record AnalyticsDeliverySnapshot(int? SourceRequestId, Guid? SourceJourneyId, string EventKey,
        string EventName, string ClientId, string SessionId, string? UserId, string Currency, decimal Value,
        DateTime OccurredUtc, int AttemptCount, DateTime? SentUtc);

    private async Task<Quotation> SeedAsync(bool? accepted = null, int? invoiceId = null)
    {
        await using var context = database.QuotationContext();
        var quotation = new Quotation
        {
            CustomerId = 42,
            EmployeeId = 7,
            CurrencyId = 764,
            Period = 30,
            ExpirationDate = new DateTime(2026, 12, 31),
            Subtotal = 100m,
            Vat = 7m,
            Total = 107m,
            WithholdingTax = 3m,
            Comment = "synthetic quotation68",
            InvoiceId = invoiceId,
            Accepted = accepted,
            CreatedDate = SeedTime,
            ModifiedDate = SeedTime,
            AcceptedUtc = accepted == true ? SeedTime : null,
            AcceptanceOrigin = accepted == true ? "employee" : null,
            SourceRequestId = 17,
            SourceJourneyId = Guid.Parse("21f4af9a-a3bf-4ca8-af29-626dc44f89cf"),
        };
        context.Quotations.Add(quotation);
        await context.SaveChangesAsync();
        context.OrderLinks.Add(new QuotationOrderLink { QuotationId = quotation.Id, OrderId = 701, CreatedDate = SeedTime, ModifiedDate = SeedTime });
        if (accepted == true)
        {
            context.AcceptedOutcomes.Add(new QuotationAcceptedOutcome
            {
                QuotationId = quotation.Id,
                EventKey = $"quotation-{quotation.Id}:accepted:v1",
                AcceptedUtc = SeedTime,
                AcceptanceOrigin = "employee",
                SourceRequestId = quotation.SourceRequestId,
                SourceJourneyId = quotation.SourceJourneyId,
            });
        }
        await context.SaveChangesAsync();
        return quotation;
    }

    private async Task AssertUnchangedAsync(Quotation seeded, int outcomeCount)
    {
        await using var read = database.QuotationContext();
        var stored = await read.Quotations.AsNoTracking().SingleAsync(value => value.Id == seeded.Id);
        Assert.Equal(seeded.Accepted, stored.Accepted);
        Assert.Equal(seeded.InvoiceId, stored.InvoiceId);
        Assert.Equal(seeded.ModifiedDate, stored.ModifiedDate);
        Assert.Equal(seeded.AcceptedUtc, stored.AcceptedUtc);
        Assert.Equal(seeded.AcceptanceOrigin, stored.AcceptanceOrigin);
        Assert.Equal(outcomeCount, await read.AcceptedOutcomes.CountAsync(value => value.QuotationId == seeded.Id));
    }

    private async Task<WebApplication> StartAppAsync(IInterceptor? interceptor = null, FixtureOrderHandler? orders = null,
        DateTimeOffset? clock = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => database.QuotationContext(interceptor));
        builder.Services.AddScoped(_ => database.RequestContext());
        builder.Services.AddSingleton<IQuotationCache, FixtureCache>();
        builder.Services.AddSingleton(Mock.Of<IIdempotencyStore>());
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(clock ?? new DateTimeOffset(2026, 9, 30, 4, 5, 6, TimeSpan.Zero)));
        builder.Services.AddScoped<IQuotationService, QuotationRepository>();
        builder.Services.AddScoped<IQuotationDecisionWorkflow, QuotationDecisionWorkflow>();
        builder.Services.AddHttpClient<IOrderDecisionClient, OrderDecisionClient>(client => client.BaseAddress = new Uri("http://fixture-orders/"))
            .ConfigurePrimaryHttpMessageHandler(() => orders ?? new FixtureOrderHandler());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:PublicKey"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(database.SigningKey.ExportSubjectPublicKeyInfoPem())),
            ["Jwt:Issuer"] = "https://fixture-iam.example",
            ["Jwt:Audience"] = "fixture-quotation",
        });
        builder.AddJwtAuthentication();
        // Only IAM permission lookup is stubbed; keep the production RS256 authentication handler.
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, DefaultAuthorizationPolicyProvider>();
        builder.Services.AddAuthorization(options => options.AddPolicy(
            $"Permission:{QuotationPermissions.QuotationsUpdate}:critical:resource_{Uri.EscapeDataString("/quotations/{quotationId}")}:live_check",
            policy => policy.RequireAuthenticatedUser().RequireClaim("permissions", QuotationPermissions.QuotationsUpdate)));
        builder.Services.AddControllers().AddApplicationPart(typeof(QuotationsController).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.PropertyNamingPolicy = null);
        var app = builder.Build();
        // Fixture-only exception translation keeps a database fault observable over HTTP; not a production status assertion.
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (DbUpdateException) { context.Response.StatusCode = 503; }
        });
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private async Task<HttpResponseMessage> DecideAsync(HttpClient client, int id, int? invoiceId,
        bool accepted = true, bool employeeInitiated = true, DateTime? expected = null,
        string profile = "employee", bool consented = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{id}/decision")
        {
            Content = consented
                ? JsonContent.Create(new
                {
                    Accepted = accepted,
                    EmployeeInitiated = employeeInitiated,
                    InvoiceId = invoiceId,
                    ClientId = " 123.456 ",
                    SessionId = "789",
                    UserId = "opaque-7",
                    Currency = "thb"
                },
                    options: SourceRequestJson)
                : JsonContent.Create(new { Accepted = accepted, EmployeeInitiated = employeeInitiated, InvoiceId = invoiceId }),
        };
        if (consented)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(new[] { "Accepted", "EmployeeInitiated", "InvoiceId", "ClientId", "SessionId", "UserId", "Currency" },
                body.RootElement.EnumerateObject().Select(property => property.Name));
            Assert.Equal("application/json", request.Content.Headers.ContentType?.MediaType);
            Assert.Equal("utf-8", request.Content.Headers.ContentType?.CharSet);
        }
        if (expected is not null) request.Headers.Add("X-Expected-Modified-Date", DateTime.SpecifyKind(expected.Value, DateTimeKind.Utc).ToString("O"));
        Authenticate(request, profile);
        return await client.SendAsync(request);
    }

    private void Authenticate(HttpRequestMessage request, string profile = "employee")
    {
        List<Claim> claims = [new("sub", profile.StartsWith("employee", StringComparison.Ordinal) ? "employee:7"
                : profile.StartsWith("intranet", StringComparison.Ordinal) ? "service:legacy-intranet" : $"service:legacy-{profile}"),
            new("identity_kind", profile.StartsWith("employee", StringComparison.Ordinal) && profile != "employee-service-kind" ? "employee" : "service"),
            new("permissions", QuotationPermissions.QuotationsUpdate)];
        if (profile.StartsWith("employee", StringComparison.Ordinal) && profile != "employee-no-role") claims.Add(new("role", "Employee"));
        if (profile == "employee-role-only") claims.RemoveAll(claim => claim.Type == "identity_kind");
        if (profile == "employee-duplicate-sub") claims.Add(new("sub", "employee:7"));
        if (profile == "employee-duplicate-kind") claims.Add(new("identity_kind", "employee"));
        if (profile == "employee-conflicting-alias") claims.Add(new(ClaimTypes.NameIdentifier, "employee:8"));
        if (profile == "employee-service-prefix")
        {
            claims.RemoveAll(claim => claim.Type == "sub");
            claims.Add(new("sub", "service:legacy-intranet"));
        }
        if (profile == "employee-customer-kind")
        {
            claims.RemoveAll(claim => claim.Type == "identity_kind");
            claims.Add(new("identity_kind", "customer"));
        }
        if (profile.Contains("wildcard", StringComparison.Ordinal)) claims.Add(new("permissions", "quotation.*"));
        if (profile == "intranet-singular") claims.Add(new("permission", QuotationPermissions.QuotationsUpdate));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken("https://fixture-iam.example", "fixture-quotation", claims,
            now.AddMinutes(-1), now.AddMinutes(5), new SigningCredentials(new RsaSecurityKey(database.SigningKey), SecurityAlgorithms.RsaSha256));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    private sealed class FixtureCache : IQuotationCache
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) where T : class => Task.FromResult<T?>(null);
        public Task SetAsync<T>(string key, T value, TimeSpan lifetime, CancellationToken cancellationToken) where T : class => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FixtureOrderHandler(int unavailableRequests = 0) : HttpMessageHandler
    {
        private int requestsSeen;
        public ConcurrentQueue<(string Path, string Key)> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Enqueue((request.RequestUri!.AbsolutePath, request.Headers.GetValues("Idempotency-Key").Single()));
            return Task.FromResult(new HttpResponseMessage(Interlocked.Increment(ref requestsSeen) <= unavailableRequests
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Created));
        }
    }

    private sealed class TwoDecisionSaveBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource bothArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref arrivals) == 2) bothArrived.TrySetResult();
            await bothArrived.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            return result;
        }
    }
}

/// <summary>One test-owned PostgreSQL18 container with distinct quotation/request databases.</summary>
public sealed class QuotationInvoiceDecisionPostgresFixture : IAsyncLifetime
{
    public RSA SigningKey { get; } = RSA.Create(2048);
    private Infrastructure.DisposableContainerSingle? containers;
    private PostgreSqlContainer postgres => (PostgreSqlContainer)containers!.Container;
    public string QuotationConnectionString => Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString());
    private string RequestConnectionString => new NpgsqlConnectionStringBuilder(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())) { Database = "quotation68_requests" }.ConnectionString;

    public async Task InitializeAsync()
    {
        containers = await Infrastructure.DisposableContainerSingle.StartAsync("quotation100-invoice-decision",
            attempt => new PostgreSqlBuilder("postgres:18-alpine").WithDockerEndpoint(attempt.Endpoint)
                .WithName(attempt.Name).WithLabel(attempt.Labels).Build());
        await using (var connection = new NpgsqlConnection(Infrastructure.DisposablePostgresConnectionPolicy.Isolate(postgres.GetConnectionString())))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE DATABASE quotation68_requests", connection);
            await command.ExecuteNonQueryAsync();
        }
        await using var quotations = QuotationContext();
        await quotations.Database.MigrateAsync();
        await using var requests = RequestContext();
        await requests.Database.MigrateAsync();
    }

    public async Task DisposeAsync() { try { if (containers is not null) await containers.DisposeAsync(); } finally { SigningKey.Dispose(); } }
    public QuotationDbContext QuotationContext(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(QuotationConnectionString);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new(options.Options);
    }
    public QuotationRequestDbContext RequestContext() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(RequestConnectionString).Options);
}
