using System.Text.Json;
using Legacy.Maliev.QuotationService.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Legacy.Maliev.QuotationService.Tests.Data;

public sealed class LegacyQuotationOutcomeAdopterTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public Task InitializeAsync() => postgres.StartAsync();

    public async Task DisposeAsync() => await postgres.DisposeAsync();

    [Fact]
    public async Task AdoptAsync_ExactSourceFacts_PreservesEveryValueAndNextIdentity()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
        LegacyQuotationOutcomeBatch batch = LoadFixture();

        LegacyQuotationOutcomeAdoptionResult result = await new LegacyQuotationOutcomeAdopter(context)
            .AdoptAsync(batch, CancellationToken.None);

        Assert.Equal(new LegacyQuotationOutcomeAdoptionResult(2, 0, 19), result);
        Assert.Equal(
            batch.Facts,
            (await context.AcceptedOutcomes.AsNoTracking().OrderBy(value => value.Id).ToListAsync())
                .Select(Map)
                .ToArray());
        Assert.Equal(19, await InsertRuntimeOutcomeAndReturnIdAsync(context));
    }

    [Fact]
    public async Task AdoptAsync_SameBatchReplayed_IsIdempotentAndKeepsNextIdentity()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
        LegacyQuotationOutcomeBatch batch = LoadFixture();
        var adopter = new LegacyQuotationOutcomeAdopter(context);
        _ = await adopter.AdoptAsync(batch, CancellationToken.None);

        LegacyQuotationOutcomeAdoptionResult replay = await adopter.AdoptAsync(batch, CancellationToken.None);

        Assert.Equal(new LegacyQuotationOutcomeAdoptionResult(0, 2, 19), replay);
        Assert.Equal(2, await context.AcceptedOutcomes.CountAsync());
        Assert.Equal(19, await InsertRuntimeOutcomeAndReturnIdAsync(context));
    }

    [Fact]
    public async Task AdoptAsync_ConcurrentReplay_SerializesIntoOneInsertAndOneReplay()
    {
        await using var setup = Context();
        await setup.Database.MigrateAsync();
        await using var firstContext = Context();
        await using var secondContext = Context();
        LegacyQuotationOutcomeBatch batch = LoadFixture();

        LegacyQuotationOutcomeAdoptionResult[] results = await Task.WhenAll(
            new LegacyQuotationOutcomeAdopter(firstContext).AdoptAsync(batch, CancellationToken.None),
            new LegacyQuotationOutcomeAdopter(secondContext).AdoptAsync(batch, CancellationToken.None));

        Assert.Equal(2, results.Sum(value => value.InsertedCount));
        Assert.Equal(2, results.Sum(value => value.ReplayedCount));
        setup.ChangeTracker.Clear();
        Assert.Equal(2, await setup.AcceptedOutcomes.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task AdoptAsync_IdentityOrEventConflict_FailsWithoutChangingCanonicalData()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
        LegacyQuotationOutcomeBatch batch = LoadFixture();
        context.AcceptedOutcomes.Add(new QuotationAcceptedOutcome
        {
            Id = 7,
            EventKey = "different-event",
            QuotationId = 99,
            AcceptedUtc = new DateTime(2026, 1, 1),
            AcceptanceOrigin = "employee",
        });
        await context.SaveChangesAsync();

        LegacyQuotationOutcomeAdoptionException exception = await Assert.ThrowsAsync<LegacyQuotationOutcomeAdoptionException>(
            () => new LegacyQuotationOutcomeAdopter(context).AdoptAsync(batch, CancellationToken.None));

        Assert.Equal("legacy_outcome_conflict", exception.Code);
        Assert.Single(await context.AcceptedOutcomes.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task AdoptAsync_DatabaseFailure_RollsBackFactsAndIdentitySequence()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION reject_second_legacy_outcome() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW."ID" = 18 THEN RAISE EXCEPTION 'forced adoption failure'; END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER reject_second_legacy_outcome
            BEFORE INSERT ON "QuotationAcceptedOutcome"
            FOR EACH ROW EXECUTE FUNCTION reject_second_legacy_outcome();
            """);

        var adopter = new LegacyQuotationOutcomeAdopter(context);
        await Assert.ThrowsAsync<DbUpdateException>(
            () => adopter.AdoptAsync(LoadFixture(), CancellationToken.None));

        Assert.Empty(await context.AcceptedOutcomes.AsNoTracking().ToListAsync());
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_second_legacy_outcome ON \"QuotationAcceptedOutcome\";");
        Assert.Equal(
            new LegacyQuotationOutcomeAdoptionResult(2, 0, 19),
            await adopter.AdoptAsync(LoadFixture(), CancellationToken.None));
        Assert.Equal(19, await InsertRuntimeOutcomeAndReturnIdAsync(context));
    }

    [Fact]
    public async Task EfMigrate_ExistingExactShadowObjects_PreservesThemWithoutSynthesizingOutcomes()
    {
        await using var context = Context();
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE SCHEMA legacy_shadow;
            CREATE TABLE legacy_shadow."QuotationOutcomeOutbox" (
                "ID" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
                "EventKey" varchar(128) NOT NULL UNIQUE,
                "QuotationID" integer NOT NULL,
                "SourceRequestID" integer NULL,
                "SourceJourneyID" uuid NULL,
                "AcceptedUtc" timestamp without time zone NOT NULL,
                "AcceptanceOrigin" varchar(16) NOT NULL);
            INSERT INTO legacy_shadow."QuotationOutcomeOutbox"
                ("ID", "EventKey", "QuotationID", "AcceptedUtc", "AcceptanceOrigin")
            VALUES (7, 'quotation-42001:accepted:v1', 42001, TIMESTAMP '2026-08-23 04:05:06.123456', 'employee');
            """);

        await context.Database.MigrateAsync();

        Assert.Equal(1, await ScalarAsync<int>(context, "SELECT COUNT(*)::int FROM legacy_shadow.\"QuotationOutcomeOutbox\";"));
        Assert.Empty(await context.AcceptedOutcomes.AsNoTracking().ToListAsync());
        Assert.Equal(
            context.Database.GetMigrations().Count(),
            await ScalarAsync<int>(context, "SELECT COUNT(*)::int FROM \"__EFMigrationsHistory\";"));
    }

    private QuotationDbContext Context() => new(
        new DbContextOptionsBuilder<QuotationDbContext>()
            .UseNpgsql(postgres.GetConnectionString())
            .Options);

    private static LegacyQuotationOutcomeBatch LoadFixture()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "quotation-outcome-outbox.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        return new(
            root.GetProperty("sourceTable").GetString()!,
            root.GetProperty("nextIdentityValue").GetInt64(),
            root.GetProperty("rows").EnumerateArray().Select(row => new LegacyQuotationOutcomeFact(
                row.GetProperty("id").GetInt64(),
                row.GetProperty("eventKey").GetString()!,
                row.GetProperty("quotationId").GetInt32(),
                row.GetProperty("sourceRequestId").ValueKind == JsonValueKind.Null ? null : row.GetProperty("sourceRequestId").GetInt32(),
                row.GetProperty("sourceJourneyId").ValueKind == JsonValueKind.Null ? null : row.GetProperty("sourceJourneyId").GetGuid(),
                DateTime.SpecifyKind(row.GetProperty("acceptedUtc").GetDateTime(), DateTimeKind.Unspecified),
                row.GetProperty("acceptanceOrigin").GetString()!)).ToArray());
    }

    private static LegacyQuotationOutcomeFact Map(QuotationAcceptedOutcome value) => new(
        value.Id,
        value.EventKey,
        value.QuotationId,
        value.SourceRequestId,
        value.SourceJourneyId,
        value.AcceptedUtc.AddTicks(value.AcceptedUtcSubMicrosecondTicks),
        value.AcceptanceOrigin);

    private static async Task<long> InsertRuntimeOutcomeAndReturnIdAsync(QuotationDbContext context)
    {
        var outcome = new QuotationAcceptedOutcome
        {
            EventKey = $"runtime-{Guid.NewGuid():N}",
            QuotationId = 99999,
            AcceptedUtc = new DateTime(2026, 8, 31),
            AcceptanceOrigin = "employee",
        };
        context.AcceptedOutcomes.Add(outcome);
        await context.SaveChangesAsync();
        return outcome.Id;
    }

    private static async Task<T> ScalarAsync<T>(QuotationDbContext context, string sql)
    {
        await using NpgsqlCommand command = ((NpgsqlConnection)context.Database.GetDbConnection()).CreateCommand();
        command.CommandText = sql;
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        return (T)(await command.ExecuteScalarAsync())!;
    }
}
