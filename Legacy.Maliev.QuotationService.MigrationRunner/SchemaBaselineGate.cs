using Npgsql;

namespace Legacy.Maliev.QuotationService.MigrationRunner;

public sealed class SchemaBaselineRejectedException(string message) : Exception(message);

public sealed class SchemaBaselineGate(ISchemaBaselineReceiptVerifier verifier)
{
    public async Task EnsureSafeAsync(
        NpgsqlConnection connection,
        SchemaBaselineExpectation expectation,
        SignedSchemaBaselineReceipt? receipt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE')",
            connection);
        var nonEmpty = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
        if (!nonEmpty)
        {
            return;
        }

        if (receipt is null)
        {
            throw new SchemaBaselineRejectedException("A signed production schema-baseline receipt is required for a non-empty database.");
        }

        var result = verifier.Verify(receipt, expectation);
        if (!result.IsValid)
        {
            throw new SchemaBaselineRejectedException($"The schema-baseline receipt was rejected: {result.Reason}.");
        }
    }
}
