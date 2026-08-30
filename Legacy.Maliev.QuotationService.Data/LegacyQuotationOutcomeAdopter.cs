using System.Data;
using Legacy.Maliev.QuotationService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Legacy.Maliev.QuotationService.Data;

/// <summary>An exact row from the legacy SQL Server dbo.QuotationOutcomeOutbox table.</summary>
public sealed record LegacyQuotationOutcomeFact(
    long Id,
    string EventKey,
    int QuotationId,
    int? SourceRequestId,
    Guid? SourceJourneyId,
    DateTime AcceptedUtc,
    string AcceptanceOrigin);

/// <summary>A reconciled legacy outcome inventory and its next SQL Server identity value.</summary>
public sealed record LegacyQuotationOutcomeBatch(
    string SourceTable,
    long NextIdentityValue,
    IReadOnlyList<LegacyQuotationOutcomeFact> Facts);

/// <summary>Result of adopting an exact legacy outcome inventory.</summary>
public sealed record LegacyQuotationOutcomeAdoptionResult(
    int InsertedCount,
    int ReplayedCount,
    long NextIdentityValue);

/// <summary>A fail-closed legacy outcome adoption error.</summary>
public sealed class LegacyQuotationOutcomeAdoptionException(string code, string message) : InvalidOperationException(message)
{
    /// <summary>Stable machine-readable failure code.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Adopts authenticated legacy outcome facts into the PostgreSQL-owned canonical table.
/// It never derives or synthesizes an outcome from quotation state.
/// </summary>
public sealed class LegacyQuotationOutcomeAdopter(QuotationDbContext context)
{
    private const string ExpectedSourceTable = "dbo.QuotationOutcomeOutbox";

    /// <summary>Adopts one exact source inventory atomically and replay-safely.</summary>
    public async Task<LegacyQuotationOutcomeAdoptionResult> AdoptAsync(
        LegacyQuotationOutcomeBatch batch,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Validate(batch);

        await using var transaction = await context.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken).ConfigureAwait(false);
        await AcquireAdoptionLockAsync(cancellationToken).ConfigureAwait(false);

        long[] ids = batch.Facts.Select(value => value.Id).ToArray();
        string[] eventKeys = batch.Facts.Select(value => value.EventKey).ToArray();
        List<QuotationAcceptedOutcome> existing = await context.AcceptedOutcomes
            .Where(value => ids.Contains(value.Id) || eventKeys.Contains(value.EventKey))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        int canonicalCount = await context.AcceptedOutcomes.CountAsync(cancellationToken).ConfigureAwait(false);
        if (canonicalCount != existing.Count)
        {
            throw Conflict("The canonical table contains outcomes outside the reconciled legacy inventory.");
        }

        var existingById = existing.ToDictionary(value => value.Id);
        var existingByEvent = existing.ToDictionary(value => value.EventKey, StringComparer.Ordinal);
        var additions = new List<QuotationAcceptedOutcome>();
        foreach (LegacyQuotationOutcomeFact fact in batch.Facts)
        {
            bool hasId = existingById.TryGetValue(fact.Id, out QuotationAcceptedOutcome? byId);
            bool hasEvent = existingByEvent.TryGetValue(fact.EventKey, out QuotationAcceptedOutcome? byEvent);
            if (hasId || hasEvent)
            {
                if (!hasId || !hasEvent || !ReferenceEquals(byId, byEvent) || !Matches(byId!, fact))
                {
                    throw Conflict($"Legacy outcome {fact.Id} conflicts with canonical identity or event ownership.");
                }

                continue;
            }

            additions.Add(new QuotationAcceptedOutcome
            {
                Id = fact.Id,
                EventKey = fact.EventKey,
                QuotationId = fact.QuotationId,
                SourceRequestId = fact.SourceRequestId,
                SourceJourneyId = fact.SourceJourneyId,
                AcceptedUtc = TruncateToMicroseconds(fact.AcceptedUtc),
                AcceptedUtcSubMicrosecondTicks = SubMicrosecondTicks(fact.AcceptedUtc),
                AcceptanceOrigin = fact.AcceptanceOrigin,
            });
        }

        context.AcceptedOutcomes.AddRange(additions);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await SetNextIdentityAsync(batch.NextIdentityValue, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (QuotationAcceptedOutcome addition in additions)
            {
                context.Entry(addition).State = EntityState.Detached;
            }

            throw;
        }

        return new(additions.Count, batch.Facts.Count - additions.Count, batch.NextIdentityValue);
    }

    private static void Validate(LegacyQuotationOutcomeBatch batch)
    {
        if (!string.Equals(batch.SourceTable, ExpectedSourceTable, StringComparison.Ordinal))
        {
            throw Invalid("legacy_outcome_source_invalid", "Only the exact dbo.QuotationOutcomeOutbox source is accepted.");
        }

        if (batch.Facts is null || batch.NextIdentityValue < 1)
        {
            throw Invalid("legacy_outcome_inventory_invalid", "The source inventory or next identity value is invalid.");
        }

        if (batch.Facts.Select(value => value.Id).Distinct().Count() != batch.Facts.Count ||
            batch.Facts.Select(value => value.EventKey).Distinct(StringComparer.Ordinal).Count() != batch.Facts.Count)
        {
            throw Invalid("legacy_outcome_inventory_invalid", "Source identities and event keys must be unique.");
        }

        foreach (LegacyQuotationOutcomeFact fact in batch.Facts)
        {
            if (fact.Id < 1 || fact.Id >= batch.NextIdentityValue || fact.QuotationId < 1 ||
                string.IsNullOrEmpty(fact.EventKey) || fact.EventKey.Length > 128 ||
                string.IsNullOrEmpty(fact.AcceptanceOrigin) || fact.AcceptanceOrigin.Length > 16 ||
                fact.AcceptedUtc.Kind != DateTimeKind.Unspecified)
            {
                throw Invalid("legacy_outcome_inventory_invalid", "A source outcome violates the exact legacy schema contract.");
            }
        }
    }

    private static bool Matches(QuotationAcceptedOutcome target, LegacyQuotationOutcomeFact source) =>
        target.Id == source.Id &&
        string.Equals(target.EventKey, source.EventKey, StringComparison.Ordinal) &&
        target.QuotationId == source.QuotationId &&
        target.SourceRequestId == source.SourceRequestId &&
        target.SourceJourneyId == source.SourceJourneyId &&
        target.AcceptedUtc.AddTicks(target.AcceptedUtcSubMicrosecondTicks) == source.AcceptedUtc &&
        string.Equals(target.AcceptanceOrigin, source.AcceptanceOrigin, StringComparison.Ordinal);

    private async Task SetNextIdentityAsync(long nextIdentityValue, CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText =
            "SELECT setval(pg_get_serial_sequence('\"QuotationAcceptedOutcome\"', 'ID'), $1, $2);";
        _ = command.Parameters.AddWithValue(nextIdentityValue == 1 ? 1L : nextIdentityValue - 1L);
        _ = command.Parameters.AddWithValue(nextIdentityValue != 1);
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AcquireAdoptionLockAsync(CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = (NpgsqlTransaction)context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = "SELECT pg_advisory_xact_lock(4861294633252357964);";
        _ = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static LegacyQuotationOutcomeAdoptionException Conflict(string message) =>
        new("legacy_outcome_conflict", message);

    private static LegacyQuotationOutcomeAdoptionException Invalid(string code, string message) =>
        new(code, message);

    internal static DateTime TruncateToMicroseconds(DateTime value) => value.AddTicks(-(value.Ticks % 10));

    internal static short SubMicrosecondTicks(DateTime value) => (short)(value.Ticks % 10);
}
