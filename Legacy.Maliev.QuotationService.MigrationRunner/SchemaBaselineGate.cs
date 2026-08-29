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
            """
            WITH user_namespaces AS (
                SELECT oid, nspname
                FROM pg_namespace
                WHERE nspname = 'public'
                   OR (nspowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                       AND nspname !~ '^pg_' AND nspname <> 'information_schema')
            )
            SELECT EXISTS (
                SELECT 1 FROM pg_class WHERE relnamespace IN (SELECT oid FROM user_namespaces)
                    AND relkind IN ('r','p','v','m','S','f')
                UNION ALL
                SELECT 1 FROM pg_type WHERE typnamespace IN (SELECT oid FROM user_namespaces)
                    AND typtype IN ('d','e','r','m') AND typisdefined
                UNION ALL
                SELECT 1 FROM pg_proc WHERE pronamespace IN (SELECT oid FROM user_namespaces)
                UNION ALL
                SELECT 1 FROM pg_operator WHERE oprnamespace IN (SELECT oid FROM user_namespaces)
                UNION ALL
                SELECT 1 FROM pg_collation WHERE collnamespace IN (SELECT oid FROM user_namespaces)
                UNION ALL
                SELECT 1 FROM pg_conversion WHERE connamespace IN (SELECT oid FROM user_namespaces)
                UNION ALL
                SELECT 1 FROM pg_ts_config WHERE cfgnamespace IN (SELECT oid FROM user_namespaces)
                UNION ALL
                SELECT 1 FROM pg_ts_dict WHERE dictnamespace IN (SELECT oid FROM user_namespaces)
                UNION ALL
                SELECT 1 FROM pg_ts_parser WHERE prsnamespace IN (SELECT oid FROM user_namespaces)
                UNION ALL
                SELECT 1 FROM pg_ts_template WHERE tmplnamespace IN (SELECT oid FROM user_namespaces)
                UNION ALL
                SELECT 1 FROM user_namespaces WHERE nspname <> 'public'
            )
            """,
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
