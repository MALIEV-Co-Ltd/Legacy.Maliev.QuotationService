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
            WITH RECURSIVE extension_objects(classid, objid) AS (
                SELECT dependency.classid, dependency.objid
                FROM pg_depend AS dependency
                WHERE dependency.refclassid = 'pg_extension'::regclass
                  AND dependency.deptype = 'e'
                UNION
                SELECT dependency.classid, dependency.objid
                FROM pg_depend AS dependency
                INNER JOIN extension_objects AS extension_object
                    ON dependency.refclassid = extension_object.classid
                   AND dependency.refobjid = extension_object.objid
                WHERE dependency.deptype IN ('a', 'i')
            ),
            user_namespaces AS (
                SELECT oid, nspname
                FROM pg_namespace
                WHERE nspname = 'public'
                   OR (nspowner = (SELECT oid FROM pg_roles WHERE rolname = current_user)
                       AND nspname !~ '^pg_' AND nspname <> 'information_schema')
            )
            SELECT EXISTS (
                SELECT 1 FROM pg_class AS candidate
                WHERE candidate.relnamespace IN (SELECT oid FROM user_namespaces)
                    AND candidate.relkind IN ('r','p','v','m','S','f')
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_class'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM pg_type AS candidate
                WHERE candidate.typnamespace IN (SELECT oid FROM user_namespaces)
                    AND candidate.typtype IN ('b','c','d','e','r','m')
                    AND candidate.typisdefined
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_type'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM pg_proc AS candidate
                WHERE candidate.pronamespace IN (SELECT oid FROM user_namespaces)
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_proc'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM pg_operator AS candidate
                WHERE candidate.oprnamespace IN (SELECT oid FROM user_namespaces)
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_operator'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM pg_collation AS candidate
                WHERE candidate.collnamespace IN (SELECT oid FROM user_namespaces)
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_collation'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM pg_conversion AS candidate
                WHERE candidate.connamespace IN (SELECT oid FROM user_namespaces)
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_conversion'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM pg_ts_config AS candidate
                WHERE candidate.cfgnamespace IN (SELECT oid FROM user_namespaces)
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_ts_config'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM pg_ts_dict AS candidate
                WHERE candidate.dictnamespace IN (SELECT oid FROM user_namespaces)
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_ts_dict'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM pg_ts_parser AS candidate
                WHERE candidate.prsnamespace IN (SELECT oid FROM user_namespaces)
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_ts_parser'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM pg_ts_template AS candidate
                WHERE candidate.tmplnamespace IN (SELECT oid FROM user_namespaces)
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_ts_template'::regclass AND objid = candidate.oid)
                UNION ALL
                SELECT 1 FROM user_namespaces AS candidate
                WHERE candidate.nspname <> 'public'
                    AND NOT EXISTS (
                        SELECT 1 FROM extension_objects
                        WHERE classid = 'pg_namespace'::regclass AND objid = candidate.oid)
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
