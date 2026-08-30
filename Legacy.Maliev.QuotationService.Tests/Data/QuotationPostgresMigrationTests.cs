using System.Data.Common;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Legacy.Maliev.QuotationService.Application.Services;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Data;

public sealed class QuotationPostgresMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer quotationPostgres = new PostgreSqlBuilder("postgres:18-alpine").Build();
    private readonly PostgreSqlContainer requestPostgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => Task.WhenAll(quotationPostgres.StartAsync(), requestPostgres.StartAsync());
    public async Task DisposeAsync() { await quotationPostgres.DisposeAsync(); await requestPostgres.DisposeAsync(); }

    [Fact]
    public async Task InitialMigrations_PreserveFinancialRequestFileAndDocumentBehavior()
    {
        await using var quotationContext = QuotationContext(); await using var requestContext = RequestContext();
        await Task.WhenAll(quotationContext.Database.MigrateAsync(), requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(QuotationRequest(), CancellationToken.None);
        var line = await repository.CreateOrderItemAsync(new(quotation.Id, null, "Legacy line", 2, 50m), CancellationToken.None);
        var file = await repository.CreateQuotationFileAsync(quotation.Id, "legacy-quotes", "quotes/1.pdf", CancellationToken.None);
        var order = await repository.CreateOrderLinkAsync(quotation.Id, 77, CancellationToken.None);
        var request = await repository.CreateRequestAsync(new("Ada", "Lovelace", "ada@example.test", "02", "TH", "MALIEV", "TAX", "quote", "internal", null), CancellationToken.None);
        var requestFile = await repository.CreateRequestFileAsync(request.Id, "legacy-requests", "requests/1.stl", CancellationToken.None);
        var loaded = await repository.GetQuotationAsync(quotation.Id, CancellationToken.None);
        var snapshot = await repository.GetDocumentSnapshotAsync(quotation.Id, CancellationToken.None);

        Assert.Equal(104m, loaded?.QuotedAmount); Assert.Equal(100m, line.Subtotal); Assert.Equal("quotes/1.pdf", file?.ObjectName); Assert.Equal(77, order?.OrderId);
        Assert.Equal("Ada", request.FirstName); Assert.Equal("requests/1.stl", requestFile?.ObjectName);
        Assert.Single(snapshot!.OrderItems); Assert.Single(snapshot.Files);
        Assert.Equal(5, await quotationContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('Quotation','OrderItem','QuotationFile','QuotationHasOrder','QuotationAcceptedOutcome')").SingleAsync());
        Assert.Equal(3, await requestContext.Database.SqlQueryRaw<int>("SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('Request','RequestFile','RequestCreateIdempotency')").SingleAsync());
        Assert.Equal(
            ["Fingerprint", "KeyHash", "RequestID"],
            await requestContext.Database.SqlQueryRaw<string>(
                    "SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_schema='public' AND table_name='RequestCreateIdempotency' ORDER BY column_name")
                .ToListAsync());
    }

    [Fact]
    public async Task ExpiryStatsAndModifiedDateConcurrency_AreAtomicAndDeterministic()
    {
        await using var quotationContext = QuotationContext(); await using var requestContext = RequestContext();
        await Task.WhenAll(quotationContext.Database.MigrateAsync(), requestContext.Database.MigrateAsync()); var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(QuotationRequest(expiration: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)), CancellationToken.None);
        var stale = quotation.ModifiedDate!.Value;
        Assert.Equal(1, await repository.DeclineExpiredAsync(new DateTime(2026, 7, 15, 0, 0, 0, DateTimeKind.Unspecified), CancellationToken.None));
        quotationContext.ChangeTracker.Clear();
        Assert.Equal(UpdateResult.Conflict, await repository.UpdateQuotationAsync(quotation.Id, QuotationRequest(), new DateTimeOffset(stale), CancellationToken.None));
        var stats = await repository.GetStatsAsync(CancellationToken.None); Assert.Equal(0, stats.Accepted); Assert.Equal(1, stats.Declined); Assert.Equal(0, stats.Open);
        Assert.Equal(3m, await repository.GetWithholdingTaxAsync(quotation.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Stats_UseOnePostgresCommandAndPreserveNullableStatusBuckets()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var setupRepository = Repository(setupQuotationContext, requestContext);
        var baseline = await setupRepository.GetStatsAsync(CancellationToken.None);
        var createdUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var accepted = OutcomeQuotation(createdUtc, 764, 107m, 3m); accepted.Accepted = true;
        var declined = OutcomeQuotation(createdUtc, 764, 107m, 3m); declined.Accepted = false;
        var open = OutcomeQuotation(createdUtc, 764, 107m, 3m);
        setupQuotationContext.Quotations.AddRange(accepted, declined, open);
        await setupQuotationContext.SaveChangesAsync();

        var commandCounter = new CommandCounter();
        await using var measuredQuotationContext = QuotationContext(commandCounter);
        var measuredRepository = Repository(measuredQuotationContext, requestContext);

        var stats = await measuredRepository.GetStatsAsync(CancellationToken.None);

        Assert.Equal(1, commandCounter.ReaderCommands);
        Assert.Equal(baseline.Accepted + 1, stats.Accepted);
        Assert.Equal(baseline.Declined + 1, stats.Declined);
        Assert.Equal(baseline.Open + 1, stats.Open);
    }

    [Fact]
    public async Task AcceptedDecision_PersistsFirstProviderNeutralOutcomeWithProvenanceExactlyOnce()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var acceptedUtc = new DateTimeOffset(2026, 8, 29, 3, 15, 0, TimeSpan.Zero);
        var journeyId = Guid.Parse("6cf84f7b-f909-4f4b-b573-43de8cefe789");
        var timeProvider = new FakeTimeProvider(acceptedUtc);
        var repository = Repository(quotationContext, requestContext, timeProvider);
        var quotation = await repository.CreateQuotationAsync(
            QuotationRequest(sourceRequestId: 91, sourceJourneyId: journeyId),
            CancellationToken.None);

        var first = await repository.ApplyDecisionAsync(
            quotation.Id,
            accepted: true,
            QuotationAcceptanceOrigin.Customer,
            expectedModifiedDate: null,
            CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromHours(1));
        var replay = await repository.ApplyDecisionAsync(
            quotation.Id,
            accepted: true,
            QuotationAcceptanceOrigin.Customer,
            expectedModifiedDate: null,
            CancellationToken.None);

        Assert.Equal(QuotationDecisionPersistenceStatus.Completed, first.Status);
        Assert.Equal(QuotationDecisionPersistenceStatus.Completed, replay.Status);
        quotationContext.ChangeTracker.Clear();
        var storedQuotation = await quotationContext.Quotations.SingleAsync(value => value.Id == quotation.Id);
        var outcome = Assert.Single(await quotationContext.AcceptedOutcomes.ToListAsync());
        Assert.True(storedQuotation.Accepted);
        Assert.Equal(acceptedUtc.UtcDateTime, storedQuotation.AcceptedUtc);
        Assert.Equal("customer", storedQuotation.AcceptanceOrigin);
        Assert.Equal(91, storedQuotation.SourceRequestId);
        Assert.Equal(journeyId, storedQuotation.SourceJourneyId);
        Assert.Equal($"quotation-{quotation.Id}:accepted:v1", outcome.EventKey);
        Assert.Equal(quotation.Id, outcome.QuotationId);
        Assert.Equal(91, outcome.SourceRequestId);
        Assert.Equal(journeyId, outcome.SourceJourneyId);
        Assert.Equal(acceptedUtc.UtcDateTime, outcome.AcceptedUtc);
        Assert.Equal("customer", outcome.AcceptanceOrigin);
    }

    [Fact]
    public async Task CreateAndGenericUpdate_CannotBypassAtomicAcceptedDecision()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);

        var quotation = await repository.CreateQuotationAsync(
            QuotationRequest(accepted: true),
            CancellationToken.None);
        var update = await repository.UpdateQuotationAsync(
            quotation.Id,
            QuotationRequest(accepted: true),
            expectedModifiedDate: null,
            CancellationToken.None);

        Assert.Null(quotation.Accepted);
        Assert.Equal(UpdateResult.Conflict, update);
        quotationContext.ChangeTracker.Clear();
        var stored = await quotationContext.Quotations.SingleAsync(value => value.Id == quotation.Id);
        Assert.Null(stored.Accepted);
        Assert.Null(stored.AcceptedUtc);
        Assert.Empty(await quotationContext.AcceptedOutcomes.ToListAsync());
    }

    [Fact]
    public async Task AcceptedOutcomeMigration_PreservesExistingQuotationAndAddsIndexedPostgresSchema()
    {
        await using var quotationContext = QuotationContext();
        var migrator = quotationContext.GetService<IMigrator>();
        const string previousMigration = "20260721032128_FixTimestampColumnType";
        await migrator.MigrateAsync(previousMigration);
        await quotationContext.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "Quotation"
                ("CustomerID", "Period", "ExpirationDate", "Subtotal", "Vat", "Total", "WithholdingTax", "CurrencyID", "Comment", "Accepted")
            VALUES
                (42, 30, TIMESTAMP '2026-12-31 00:00:00', 100.00, 7.00, 107.00, 3.00, 764, 'pre-outcome-migration', TRUE);
            """);
        var quotationId = await quotationContext.Database.SqlQueryRaw<int>(
            "SELECT \"ID\" AS \"Value\" FROM \"Quotation\" WHERE \"Comment\" = 'pre-outcome-migration'")
            .SingleAsync();

        await migrator.MigrateAsync();

        quotationContext.ChangeTracker.Clear();
        var preserved = await quotationContext.Quotations.SingleAsync(value => value.Id == quotationId);
        Assert.True(preserved.Accepted);
        Assert.Equal(104m, preserved.QuotedAmount);
        Assert.Null(preserved.AcceptedUtc);
        Assert.Null(preserved.AcceptanceOrigin);
        Assert.Null(preserved.SourceRequestId);
        Assert.Null(preserved.SourceJourneyId);
        Assert.Empty(await quotationContext.AcceptedOutcomes.ToListAsync());
        Assert.Equal(
        [
            "IX_QuotationAcceptedOutcome_AcceptedUtc",
            "IX_QuotationAcceptedOutcome_EventKey",
            "IX_QuotationAcceptedOutcome_QuotationID",
            "IX_QuotationAcceptedOutcome_SourceJourneyID",
            "IX_QuotationAcceptedOutcome_SourceRequestID",
        ],
            await quotationContext.Database.SqlQueryRaw<string>(
                    "SELECT indexname AS \"Value\" FROM pg_indexes WHERE schemaname = 'public' AND tablename = 'QuotationAcceptedOutcome' AND indexname LIKE 'IX_%' ORDER BY indexname")
                .ToListAsync());
        Assert.Equal(
            0,
            await quotationContext.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*)::int AS \"Value\" FROM pg_constraint WHERE conname = 'FK_QuotationAcceptedOutcome_Quotation'")
                .SingleAsync());
        Assert.Equal(
            0,
            await quotationContext.Database.SqlQueryRaw<int>(
                    "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.columns WHERE table_schema = 'public' AND table_name IN ('Quotation', 'QuotationAcceptedOutcome') AND column_name = 'xmin'")
                .SingleAsync());
    }

    [Fact]
    public void AcceptedOutcomeMigration_GeneratesAdditivePostgresOnlyUpgradeSql()
    {
        using var quotationContext = QuotationContext();
        var migrations = quotationContext.Database.GetMigrations().ToList();
        var latestMigration = migrations[^1];

        var script = quotationContext.GetService<IMigrator>().GenerateScript(
            "20260721032128_FixTimestampColumnType",
            latestMigration);

        Assert.Contains("CREATE TABLE \"QuotationAcceptedOutcome\"", script, StringComparison.Ordinal);
        Assert.Contains("CREATE UNIQUE INDEX \"IX_QuotationAcceptedOutcome_EventKey\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP TABLE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DROP COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DELETE FROM", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER COLUMN", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlServer", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("datetime2", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nvarchar", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[dbo]", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("xmin", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AcceptedOutcomeMigration_RefusesDestructiveDowngrade()
    {
        using var quotationContext = QuotationContext();
        var migrations = quotationContext.Database.GetMigrations().ToList();
        var latestMigration = migrations[^1];

        var exception = Assert.Throws<NotSupportedException>(() =>
            quotationContext.GetService<IMigrator>().GenerateScript(
                latestMigration,
                "20260721032128_FixTimestampColumnType"));

        Assert.Contains("compensating migration", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentAcceptedControllerDecisions_ConvergeToOneOutcome()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        var quotation = await Repository(setupQuotationContext, setupRequestContext)
            .CreateQuotationAsync(QuotationRequest(), CancellationToken.None);

        var updateBarrier = new DecisionUpdateBarrier();
        await using var quotationContext1 = QuotationContext(updateBarrier);
        await using var quotationContext2 = QuotationContext(updateBarrier);
        await using var requestContext1 = RequestContext();
        await using var requestContext2 = RequestContext();
        var acceptedUtc = new DateTimeOffset(2026, 8, 29, 4, 0, 0, TimeSpan.Zero);
        var repository1 = Repository(quotationContext1, requestContext1, new FakeTimeProvider(acceptedUtc));
        var repository2 = Repository(quotationContext2, requestContext2, new FakeTimeProvider(acceptedUtc));
        var controller1 = DecisionController(repository1);
        var controller2 = DecisionController(repository2);

        var responses = await Task.WhenAll(
            controller1.DecideQuotationAsync(
                quotation.Id,
                new QuotationDecisionRequest(Accepted: true, EmployeeInitiated: true),
                expected: null,
                CancellationToken.None),
            controller2.DecideQuotationAsync(
                quotation.Id,
                new QuotationDecisionRequest(Accepted: true, EmployeeInitiated: true),
                expected: null,
                CancellationToken.None));

        Assert.All(responses, response => Assert.IsType<OkObjectResult>(response));
        await using var verificationContext = QuotationContext();
        var outcome = Assert.Single(await verificationContext.AcceptedOutcomes.ToListAsync());
        Assert.Equal($"quotation-{quotation.Id}:accepted:v1", outcome.EventKey);
        Assert.Equal("employee", outcome.AcceptanceOrigin);
    }

    [Fact]
    public async Task DeclinedDecision_DoesNotCreateAcceptedOutcome()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(QuotationRequest(), CancellationToken.None);

        var result = await repository.ApplyDecisionAsync(
            quotation.Id,
            accepted: false,
            acceptanceOrigin: null,
            expectedModifiedDate: null,
            CancellationToken.None);

        Assert.Equal(QuotationDecisionPersistenceStatus.Completed, result.Status);
        quotationContext.ChangeTracker.Clear();
        var stored = await quotationContext.Quotations.SingleAsync(value => value.Id == quotation.Id);
        Assert.False(stored.Accepted);
        Assert.Null(stored.AcceptedUtc);
        Assert.Null(stored.AcceptanceOrigin);
        Assert.Empty(await quotationContext.AcceptedOutcomes.ToListAsync());
    }

    [Fact]
    public async Task DeclinedQuotation_RejectsCustomerAcceptanceButAllowsEmployeeOverride()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(QuotationRequest(), CancellationToken.None);
        await repository.ApplyDecisionAsync(
            quotation.Id,
            accepted: false,
            acceptanceOrigin: null,
            expectedModifiedDate: null,
            CancellationToken.None);

        var customerAcceptance = await repository.ApplyDecisionAsync(
            quotation.Id,
            accepted: true,
            QuotationAcceptanceOrigin.Customer,
            expectedModifiedDate: null,
            CancellationToken.None);

        Assert.Equal(QuotationDecisionPersistenceStatus.Conflict, customerAcceptance.Status);
        quotationContext.ChangeTracker.Clear();
        Assert.False((await quotationContext.Quotations.SingleAsync(value => value.Id == quotation.Id)).Accepted);
        Assert.Empty(await quotationContext.AcceptedOutcomes.ToListAsync());

        var employeeAcceptance = await repository.ApplyDecisionAsync(
            quotation.Id,
            accepted: true,
            QuotationAcceptanceOrigin.Employee,
            expectedModifiedDate: null,
            CancellationToken.None);

        Assert.Equal(QuotationDecisionPersistenceStatus.Completed, employeeAcceptance.Status);
        quotationContext.ChangeTracker.Clear();
        var stored = await quotationContext.Quotations.SingleAsync(value => value.Id == quotation.Id);
        var outcome = Assert.Single(await quotationContext.AcceptedOutcomes.ToListAsync());
        Assert.True(stored.Accepted);
        Assert.Equal("employee", stored.AcceptanceOrigin);
        Assert.Equal("employee", outcome.AcceptanceOrigin);
    }

    [Fact]
    public async Task AcceptedQuotation_DeletePreservesImmutableOutcome()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(QuotationRequest(), CancellationToken.None);
        await repository.ApplyDecisionAsync(
            quotation.Id,
            accepted: true,
            QuotationAcceptanceOrigin.Customer,
            expectedModifiedDate: null,
            CancellationToken.None);

        var deleted = await repository.DeleteQuotationAsync(quotation.Id, CancellationToken.None);

        Assert.True(deleted);
        quotationContext.ChangeTracker.Clear();
        Assert.False(await quotationContext.Quotations.AnyAsync(value => value.Id == quotation.Id));
        var outcome = Assert.Single(await quotationContext.AcceptedOutcomes.ToListAsync());
        Assert.Equal(quotation.Id, outcome.QuotationId);
        Assert.Equal($"quotation-{quotation.Id}:accepted:v1", outcome.EventKey);
    }

    [Fact]
    public async Task OutcomeReadback_UsesHalfOpenUtcWindowAndReturnsDeterministicPrivacySafeAggregates()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var fromUtc = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var toUtc = fromUtc.AddDays(2);
        var sourceJourneyId = Guid.Parse("39a14dce-9ca2-460b-a807-7ac3f7c2cb10");
        var dayZeroAccepted = OutcomeQuotation(
            fromUtc.AddHours(2),
            currencyId: 764,
            total: 107m,
            withholdingTax: 3m,
            sourceRequestId: 91,
            sourceJourneyId);
        var dayZeroOpen = OutcomeQuotation(fromUtc.AddHours(8), 764, 25m, 0m);
        var dayOneThb = OutcomeQuotation(fromUtc.AddDays(1).AddHours(6), 764, 50m, 0m);
        var dayOneUsd = OutcomeQuotation(fromUtc.AddDays(1).AddHours(3), 840, 200m, 0m);
        var exclusiveBoundary = OutcomeQuotation(toUtc, 392, 300m, 0m);
        quotationContext.Quotations.AddRange(
            dayZeroAccepted,
            dayZeroOpen,
            dayOneThb,
            dayOneUsd,
            exclusiveBoundary);
        await quotationContext.SaveChangesAsync();
        quotationContext.AcceptedOutcomes.AddRange(
            Outcome(dayZeroAccepted, fromUtc.AddHours(5), "customer"),
            Outcome(dayOneThb, fromUtc.AddDays(1).AddHours(7), "employee"),
            Outcome(dayOneUsd, fromUtc.AddDays(1).AddHours(4), "customer"),
            Outcome(exclusiveBoundary, toUtc, "customer"));
        await quotationContext.SaveChangesAsync();

        var readback = await Repository(quotationContext, requestContext)
            .GetOutcomeReadbackAsync(fromUtc, toUtc, CancellationToken.None);

        Assert.Equal(fromUtc, readback.FromUtc);
        Assert.Equal(toUtc, readback.ToUtc);
        Assert.Equal("unavailable", readback.TechnicalConversionAvailability);
        Assert.Equal("unavailable", readback.QualifiedCustomerAvailability);
        Assert.Equal("unavailable", readback.RevenueAvailability);
        Assert.Collection(
            readback.Days,
            day =>
            {
                Assert.Equal(fromUtc, day.DayUtc);
                Assert.Equal(2, day.PersistedQuotationCount);
                Assert.Equal(1, day.AcceptedQuotationCount);
                Assert.Equal(1, day.SourceAttributedPersistedQuotationCount);
                Assert.Equal(1, day.SourceAttributedAcceptedQuotationCount);
                Assert.Equal(1, day.UnattributedPersistedQuotationCount);
                Assert.Equal(0, day.UnattributedAcceptedQuotationCount);
                Assert.Equal(
                    new AcceptedQuotedAmountByCurrency(764, 104m, 1),
                    Assert.Single(day.AcceptedQuotedAmountsByCurrency));
            },
            day =>
            {
                Assert.Equal(fromUtc.AddDays(1), day.DayUtc);
                Assert.Equal(2, day.PersistedQuotationCount);
                Assert.Equal(2, day.AcceptedQuotationCount);
                Assert.Equal(0, day.SourceAttributedPersistedQuotationCount);
                Assert.Equal(0, day.SourceAttributedAcceptedQuotationCount);
                Assert.Equal(2, day.UnattributedPersistedQuotationCount);
                Assert.Equal(2, day.UnattributedAcceptedQuotationCount);
                Assert.Equal(
                [
                    new AcceptedQuotedAmountByCurrency(764, 50m, 1),
                    new AcceptedQuotedAmountByCurrency(840, 200m, 1),
                ],
                    day.AcceptedQuotedAmountsByCurrency);
            });

        var json = JsonSerializer.Serialize(readback, new JsonSerializerOptions { PropertyNamingPolicy = null });
        Assert.Contains("\"TechnicalConversionAvailability\":\"unavailable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"QualifiedCustomerAvailability\":\"unavailable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"RevenueAvailability\":\"unavailable\"", json, StringComparison.Ordinal);
        foreach (var forbiddenProperty in new[]
        {
            "QuotationId", "CustomerId", "EmployeeId", "InvoiceId", "OrderId",
            "SourceRequestId", "SourceJourneyId", "EventKey", "AcceptanceOrigin",
            "FirstName", "LastName", "Email", "TelephoneNumber", "TaxIdentification",
        })
        {
            Assert.DoesNotContain(forbiddenProperty, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task OutcomeReadback_PartialSourceKeysRemainAggregateOnlyAndCountAsUnattributed()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var fromUtc = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);
        var quotation = OutcomeQuotation(fromUtc.AddHours(1), 764, 100m, 0m, sourceRequestId: 123);
        quotationContext.Quotations.Add(quotation);
        await quotationContext.SaveChangesAsync();
        quotationContext.AcceptedOutcomes.Add(Outcome(quotation, fromUtc.AddHours(2), "customer"));
        await quotationContext.SaveChangesAsync();

        var day = Assert.Single((await Repository(quotationContext, requestContext)
            .GetOutcomeReadbackAsync(fromUtc, fromUtc.AddDays(1), CancellationToken.None)).Days);

        Assert.Equal(0, day.SourceAttributedPersistedQuotationCount);
        Assert.Equal(0, day.SourceAttributedAcceptedQuotationCount);
        Assert.Equal(1, day.UnattributedPersistedQuotationCount);
        Assert.Equal(1, day.UnattributedAcceptedQuotationCount);
        var json = JsonSerializer.Serialize(day);
        Assert.DoesNotContain("SourceRequestId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SourceJourneyId", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetQuotations_ModifiedDateDescending_ReturnsNewestNonNullActivityWithDescendingIdTieBreak()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        const int customerId = 1_000_001;
        var createdDate = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var newestModifiedDate = createdDate.AddDays(3);
        var older = QuotationRecord(customerId, createdDate, createdDate.AddDays(1));
        var newestLowerId = QuotationRecord(customerId, createdDate.AddDays(1), newestModifiedDate);
        var newestHigherId = QuotationRecord(customerId, createdDate.AddDays(2), newestModifiedDate);
        var nullModifiedDate = QuotationRecord(customerId, createdDate.AddDays(3), createdDate.AddDays(2));
        quotationContext.Quotations.AddRange(older, newestLowerId, newestHigherId, nullModifiedDate);
        await quotationContext.SaveChangesAsync();
        await quotationContext.Quotations
            .Where(value => value.Id == nullModifiedDate.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ModifiedDate, (DateTime?)null));

        var page = await repository.GetQuotationsAsync(
            customerId,
            QuotationSortType.QuotationModifiedDate_Descending,
            search: null,
            pageIndex: 1,
            pageSize: 2,
            CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal([newestHigherId.Id, newestLowerId.Id], page.Items.Select(value => value.Id));
    }

    [Fact]
    public async Task GetQuotations_CreatedDateDescending_UsesDescendingIdTieBreak()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        const int customerId = 1_000_002;
        var createdDate = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Unspecified);
        var lowestId = QuotationRecord(customerId, createdDate, createdDate);
        var middleId = QuotationRecord(customerId, createdDate, createdDate.AddMinutes(1));
        var highestId = QuotationRecord(customerId, createdDate, createdDate.AddMinutes(2));
        quotationContext.Quotations.AddRange(lowestId, middleId, highestId);
        await quotationContext.SaveChangesAsync();

        var page = await repository.GetQuotationsAsync(
            customerId,
            QuotationSortType.QuotationCreatedDate_Descending,
            search: null,
            pageIndex: 1,
            pageSize: 2,
            CancellationToken.None);

        Assert.NotNull(page);
        Assert.Equal([highestId.Id, middleId.Id], page.Items.Select(value => value.Id));
    }

    [Fact]
    public async Task CustomerQuotationDetails_EnforceOwnershipAndComposeReadOnlyMetadata()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var repository = Repository(quotationContext, requestContext);
        var quotation = await repository.CreateQuotationAsync(
            QuotationRequest(customerId: 42),
            CancellationToken.None);
        await repository.CreateOrderItemAsync(
            new(quotation.Id, 77, "Owned line", 2, 50m),
            CancellationToken.None);
        await repository.CreateOrderLinkAsync(quotation.Id, 77, CancellationToken.None);
        await repository.CreateQuotationFileAsync(
            quotation.Id,
            "legacy-quotes",
            "quotes/owned.pdf",
            CancellationToken.None);

        Assert.Null(await repository.GetCustomerQuotationAsync(41, quotation.Id, CancellationToken.None));
        var details = await repository.GetCustomerQuotationAsync(42, quotation.Id, CancellationToken.None);

        Assert.NotNull(details);
        Assert.Equal(42, details.Quotation.CustomerId);
        Assert.Equal("Owned line", Assert.Single(details.OrderItems).Description);
        Assert.Equal(77, Assert.Single(details.Orders).OrderId);
        Assert.Equal("quotes/owned.pdf", Assert.Single(details.Files).ObjectName);
    }

    [Fact]
    public async Task RequestFileCreate_RestartOrCacheLoss_ReconcilesExistingNaturalTuple()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        var setupRepository = Repository(setupQuotationContext, setupRequestContext);
        var request = await setupRepository.CreateRequestAsync(
            new("Replay", "Test", "replay@example.test", null, "TH", null, null, "request-file replay", null, null),
            CancellationToken.None);
        var first = await setupRepository.CreateRequestFileAsync(
            request.Id,
            "legacy-requests",
            "instant-quotation/replay/model.stl",
            CancellationToken.None);

        await using var restartedQuotationContext = QuotationContext();
        await using var restartedRequestContext = RequestContext();
        var restartedRepository = Repository(restartedQuotationContext, restartedRequestContext);
        var replay = await restartedRepository.CreateRequestFileAsync(
            request.Id,
            " legacy-requests ",
            " instant-quotation/replay/model.stl ",
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(first.Id, replay?.Id);
        Assert.Equal(
            1,
            await restartedRequestContext.Files.CountAsync(
                file => file.RequestId == request.Id
                    && file.Bucket == "legacy-requests"
                    && file.ObjectName == "instant-quotation/replay/model.stl"));
    }

    [Fact]
    public async Task RequestFileCreate_ConcurrentIdenticalAttempts_InsertOnce()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        var setupRepository = Repository(setupQuotationContext, setupRequestContext);
        var request = await setupRepository.CreateRequestAsync(
            new("Concurrent", "Test", "concurrent@example.test", null, "TH", null, null, "request-file concurrency", null, null),
            CancellationToken.None);

        await using var quotationContext1 = QuotationContext();
        await using var requestContext1 = RequestContext();
        await using var quotationContext2 = QuotationContext();
        await using var requestContext2 = RequestContext();
        var repository1 = Repository(quotationContext1, requestContext1);
        var repository2 = Repository(quotationContext2, requestContext2);

        var attempts = await Task.WhenAll(
            repository1.CreateRequestFileAsync(
                request.Id,
                "legacy-requests",
                "instant-quotation/concurrent/model.stl",
                CancellationToken.None),
            repository2.CreateRequestFileAsync(
                request.Id,
                "legacy-requests",
                "instant-quotation/concurrent/model.stl",
                CancellationToken.None));

        Assert.All(attempts, Assert.NotNull);
        Assert.Equal(attempts[0]!.Id, attempts[1]!.Id);
        await using var verificationContext = RequestContext();
        Assert.Equal(
            1,
            await verificationContext.Files.CountAsync(
                file => file.RequestId == request.Id
                    && file.Bucket == "legacy-requests"
                    && file.ObjectName == "instant-quotation/concurrent/model.stl"));
    }

    [Fact]
    public async Task RequestFileCreate_PreexistingDuplicateTuple_ReturnsOldestWithoutAddingAnother()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        var setupRepository = Repository(setupQuotationContext, setupRequestContext);
        var request = await setupRepository.CreateRequestAsync(
            new("Historical", "Duplicate", "historical@example.test", null, "TH", null, null, "legacy duplicate", null, null),
            CancellationToken.None);
        var now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified);
        setupRequestContext.Files.AddRange(
            new QuotationRequestFile
            {
                RequestId = request.Id,
                Bucket = "legacy-requests",
                ObjectName = "instant-quotation/historical/model.stl",
                CreatedDate = now,
                ModifiedDate = now,
            },
            new QuotationRequestFile
            {
                RequestId = request.Id,
                Bucket = "legacy-requests",
                ObjectName = "instant-quotation/historical/model.stl",
                CreatedDate = now.AddSeconds(1),
                ModifiedDate = now.AddSeconds(1),
            });
        await setupRequestContext.SaveChangesAsync();
        var oldestId = await setupRequestContext.Files
            .Where(file => file.RequestId == request.Id)
            .MinAsync(file => file.Id);

        await using var restartedQuotationContext = QuotationContext();
        await using var restartedRequestContext = RequestContext();
        var replay = await Repository(restartedQuotationContext, restartedRequestContext)
            .CreateRequestFileAsync(
                request.Id,
                "legacy-requests",
                "instant-quotation/historical/model.stl",
                CancellationToken.None);

        Assert.Equal(oldestId, replay?.Id);
        Assert.Equal(
            2,
            await restartedRequestContext.Files.CountAsync(file => file.RequestId == request.Id));
    }

    [Fact]
    public async Task RequestCreate_LostResponseAndRestart_ReplaysOriginalDurableResult()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        IQuotationService firstService = Repository(setupQuotationContext, setupRequestContext);
        var request = Request("lost-response@example.test", "Lost response");
        var keyHash = Hash("legacy-web-quotation-lost-response");
        var fingerprint = Hash("lost-response-payload");

        var first = await firstService.CreateRequestIdempotentlyAsync(
            request,
            keyHash,
            fingerprint,
            CancellationToken.None);

        await using var restartedQuotationContext = QuotationContext();
        await using var restartedRequestContext = RequestContext();
        IQuotationService restartedService = Repository(restartedQuotationContext, restartedRequestContext);
        var replay = await restartedService.CreateRequestIdempotentlyAsync(
            request,
            keyHash,
            fingerprint,
            CancellationToken.None);

        Assert.Equal(IdempotencyBindingResult.Acquired, first.Binding);
        Assert.Equal(IdempotencyBindingResult.Matched, replay.Binding);
        Assert.NotNull(first.Response);
        Assert.Equal(first.Response, replay.Response);
        Assert.Equal(
            1,
            await restartedRequestContext.Requests.CountAsync(value => value.Email == "lost-response@example.test"));
    }

    [Fact]
    public async Task RequestCreate_ConcurrentIdenticalAttempts_ConvergeToOneRequest()
    {
        await using var setupQuotationContext = QuotationContext();
        await using var setupRequestContext = RequestContext();
        await Task.WhenAll(
            setupQuotationContext.Database.MigrateAsync(),
            setupRequestContext.Database.MigrateAsync());
        await using var quotationContext1 = QuotationContext();
        await using var requestContext1 = RequestContext();
        await using var quotationContext2 = QuotationContext();
        await using var requestContext2 = RequestContext();
        IQuotationService service1 = Repository(quotationContext1, requestContext1);
        IQuotationService service2 = Repository(quotationContext2, requestContext2);
        var request = Request("concurrent-request@example.test", "Concurrent request");
        var keyHash = Hash("legacy-web-quotation-concurrent-request");
        var fingerprint = Hash("concurrent-request-payload");

        var outcomes = await Task.WhenAll(
            service1.CreateRequestIdempotentlyAsync(request, keyHash, fingerprint, CancellationToken.None),
            service2.CreateRequestIdempotentlyAsync(request, keyHash, fingerprint, CancellationToken.None));

        Assert.Contains(outcomes, value => value.Binding == IdempotencyBindingResult.Acquired);
        Assert.Contains(outcomes, value => value.Binding == IdempotencyBindingResult.Matched);
        Assert.All(outcomes, value => Assert.NotNull(value.Response));
        Assert.Equal(outcomes[0].Response!.Id, outcomes[1].Response!.Id);
        await using var verificationContext = RequestContext();
        Assert.Equal(
            1,
            await verificationContext.Requests.CountAsync(value => value.Email == "concurrent-request@example.test"));
    }

    [Fact]
    public async Task RequestCreate_SameKeyAndChangedFingerprint_FailsClosedWithoutSecondRequest()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        IQuotationService service = Repository(quotationContext, requestContext);
        var keyHash = Hash("legacy-web-quotation-conflict");
        var first = await service.CreateRequestIdempotentlyAsync(
            Request("conflict@example.test", "Original"),
            keyHash,
            Hash("original-payload"),
            CancellationToken.None);
        var conflict = await service.CreateRequestIdempotentlyAsync(
            Request("conflict@example.test", "Changed"),
            keyHash,
            Hash("changed-payload"),
            CancellationToken.None);

        Assert.Equal(IdempotencyBindingResult.Acquired, first.Binding);
        Assert.Equal(IdempotencyBindingResult.Conflict, conflict.Binding);
        Assert.Null(conflict.Response);
        Assert.Equal(
            1,
            await requestContext.Requests.CountAsync(value => value.Email == "conflict@example.test"));
    }

    [Fact]
    public async Task RequestCreate_RedisCacheUnavailable_DoesNotAffectDurableCreate()
    {
        await using var quotationContext = QuotationContext();
        await using var requestContext = RequestContext();
        await Task.WhenAll(
            quotationContext.Database.MigrateAsync(),
            requestContext.Database.MigrateAsync());
        var cache = new Mock<IQuotationCache>(MockBehavior.Strict);
        IQuotationService service = new QuotationRepository(
            quotationContext,
            requestContext,
            cache.Object,
            TimeProvider.System);

        var result = await service.CreateRequestIdempotentlyAsync(
            Request("redis-independent@example.test", "Redis independent"),
            Hash("legacy-web-quotation-redis-independent"),
            Hash("redis-independent-payload"),
            CancellationToken.None);

        Assert.Equal(IdempotencyBindingResult.Acquired, result.Binding);
        Assert.True(result.Response?.Id > 0);
        cache.VerifyNoOtherCalls();
    }

    private QuotationRepository Repository(
        QuotationDbContext quotations,
        QuotationRequestDbContext requests,
        TimeProvider? timeProvider = null)
    {
        var cache = new Mock<IQuotationCache>();
        cache.Setup(x => x.GetAsync<QuotationResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((QuotationResponse?)null);
        cache.Setup(x => x.GetAsync<QuotationRequestResponse>(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((QuotationRequestResponse?)null);
        return new(quotations, requests, cache.Object, timeProvider ?? TimeProvider.System);
    }

    private static QuotationsController DecisionController(IQuotationService service)
    {
        var controller = new QuotationsController(
            service,
            Mock.Of<IIdempotencyStore>(),
            Mock.Of<IAuthorizationService>(),
            new QuotationDecisionWorkflow(service, Mock.Of<IOrderDecisionClient>()))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, "employee-7"),
                        new Claim(ClaimTypes.Role, "Employee"),
                    ], "test")),
                },
            },
        };
        return controller;
    }

    private static UpsertQuotationRequest QuotationRequest(
        DateTime? expiration = null,
        int? customerId = null,
        bool? accepted = null,
        int? sourceRequestId = null,
        Guid? sourceJourneyId = null) =>
        new(
            customerId,
            null,
            null,
            30,
            expiration ?? new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Unspecified),
            100m,
            7m,
            107m,
            3m,
            764,
            "legacy",
            "FOB",
            "Courier",
            "30 days",
            accepted,
            sourceRequestId,
            sourceJourneyId);

    private static Quotation OutcomeQuotation(
        DateTime createdUtc,
        int currencyId,
        decimal total,
        decimal withholdingTax,
        int? sourceRequestId = null,
        Guid? sourceJourneyId = null) =>
        new()
        {
            Period = 30,
            ExpirationDate = DateTime.SpecifyKind(createdUtc.AddDays(30), DateTimeKind.Unspecified),
            Subtotal = total,
            Vat = 0m,
            Total = total,
            WithholdingTax = withholdingTax,
            CurrencyId = currencyId,
            SourceRequestId = sourceRequestId,
            SourceJourneyId = sourceJourneyId,
            CreatedDate = DateTime.SpecifyKind(createdUtc, DateTimeKind.Unspecified),
            ModifiedDate = DateTime.SpecifyKind(createdUtc, DateTimeKind.Unspecified),
        };

    private static QuotationAcceptedOutcome Outcome(
        Quotation quotation,
        DateTime acceptedUtc,
        string origin) =>
        new()
        {
            EventKey = $"quotation-{quotation.Id}:accepted:v1",
            QuotationId = quotation.Id,
            SourceRequestId = quotation.SourceRequestId,
            SourceJourneyId = quotation.SourceJourneyId,
            AcceptedUtc = DateTime.SpecifyKind(acceptedUtc, DateTimeKind.Unspecified),
            AcceptanceOrigin = origin,
        };
    private static Quotation QuotationRecord(int customerId, DateTime createdDate, DateTime? modifiedDate) => new()
    {
        CustomerId = customerId,
        Period = 30,
        ExpirationDate = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Unspecified),
        Subtotal = 100m,
        Vat = 7m,
        Total = 107m,
        WithholdingTax = 3m,
        CurrencyId = 764,
        Comment = "activity-sort-regression",
        CreatedDate = createdDate,
        ModifiedDate = modifiedDate,
    };
    private static UpsertQuotationRequestRequest Request(string email, string message) => new(
        "Ada",
        "Lovelace",
        email,
        null,
        "TH",
        null,
        null,
        message,
        null,
        null);
    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private QuotationDbContext QuotationContext(params IInterceptor[] interceptors) => new(new DbContextOptionsBuilder<QuotationDbContext>().UseNpgsql(quotationPostgres.GetConnectionString()).AddInterceptors(interceptors).Options);
    private QuotationRequestDbContext RequestContext() => new(new DbContextOptionsBuilder<QuotationRequestDbContext>().UseNpgsql(requestPostgres.GetConnectionString()).Options);

    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int readerCommands;

        public int ReaderCommands => Volatile.Read(ref readerCommands);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref readerCommands);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class DecisionUpdateBarrier : DbCommandInterceptor
    {
        private readonly TaskCompletionSource arrivals = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivalCount;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE \"Quotation\"", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivalCount) == 2)
                {
                    arrivals.TrySetResult();
                }

                await arrivals.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }
}
