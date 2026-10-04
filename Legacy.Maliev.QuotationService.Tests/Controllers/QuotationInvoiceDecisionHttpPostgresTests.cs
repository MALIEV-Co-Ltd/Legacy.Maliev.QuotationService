using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
    public async Task Decision_UntrustedEmployeeOverride_IsForbidden(string profile)
    {
        var seeded = await SeedAsync();
        await using var app = await StartAppAsync();
        using var client = app.GetTestClient();
        using var response = await DecideAsync(client, seeded.Id, 901, profile: profile);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertUnchangedAsync(seeded, 0);
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
        await using var app = await StartAppAsync();
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

    private async Task<WebApplication> StartAppAsync(IInterceptor? interceptor = null, FixtureOrderHandler? orders = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddScoped(_ => database.QuotationContext(interceptor));
        builder.Services.AddScoped(_ => database.RequestContext());
        builder.Services.AddSingleton<IQuotationCache, FixtureCache>();
        builder.Services.AddSingleton(Mock.Of<IIdempotencyStore>());
        builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 4, 5, 6, TimeSpan.Zero)));
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
        string profile = "employee")
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/quotations/{id}/decision")
        {
            Content = JsonContent.Create(new { Accepted = accepted, EmployeeInitiated = employeeInitiated, InvoiceId = invoiceId }),
        };
        if (expected is not null) request.Headers.Add("X-Expected-Modified-Date", DateTime.SpecifyKind(expected.Value, DateTimeKind.Utc).ToString("O"));
        Authenticate(request, profile);
        return await client.SendAsync(request);
    }

    private void Authenticate(HttpRequestMessage request, string profile = "employee")
    {
        List<Claim> claims = [new("sub", profile.StartsWith("employee", StringComparison.Ordinal) ? "employee:7"
                : profile.StartsWith("intranet", StringComparison.Ordinal) ? "service:legacy-intranet" : $"service:legacy-{profile}"),
            new("identity_kind", profile == "employee" ? "employee" : "service"),
            new("permissions", QuotationPermissions.QuotationsUpdate)];
        if (profile.StartsWith("employee", StringComparison.Ordinal)) claims.Add(new("role", "Employee"));
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
