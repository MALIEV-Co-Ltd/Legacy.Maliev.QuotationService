using System.Data;
using System.Data.Common;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace FinancialCompositionAdapter;

// Private owner expectations, acquired and qualified independently by the parent.
// PostgreSQL spelling and deparsed expressions must come from owner-reviewed expectations;
// this observer does not invent type aliases or normalize expression semantics.
public sealed record PhysicalColumnExpectation(
    string Name, string ModelStoreType, string PostgreSqlStoreType, bool Nullable,
    string IdentityKind, string GeneratedKind, string? PostgreSqlExpression);

public sealed record PhysicalTableExpectation(
    string Schema, string Table, IReadOnlyList<PhysicalColumnExpectation> Columns);

public sealed record QualifiedPhysicalSchemaExpectation(
    string Role, string SourceCommit, string AssemblySha256, string OwnerExpectationSha256,
    IReadOnlyList<PhysicalTableExpectation> Tables);

// Only counts and source digests are public; database names/OIDs, columns and expressions stay private.
public sealed record PhysicalColumnsObservation(
    string Role, string SourceCommit, string AssemblySha256, string OwnerExpectationSha256,
    int TableCount, int ColumnCount, bool ColumnTypesMatch, bool NullabilityMatches,
    bool DefaultsMatch, bool ComputedColumnsMatch);

public static class OwnedPhysicalColumns
{
    private const int MaximumTables = 256;
    private const int MaximumColumns = 16384;
    private const int MaximumExpressionBytes = 8192;

    public static async Task<PhysicalColumnsObservation> ObserveAsync(
        DbContext context, OwnedDatabaseExpectation database,
        QualifiedPhysicalSchemaExpectation expectation, CancellationToken cancellationToken)
    {
        // Snapshot input before any asynchronous work so a caller cannot change the contract mid-query.
        var tables = Snapshot(expectation);
        if (!PinnedBusinessSchemas.Contracts.TryGetValue(database.Role, out var owner)
            || expectation.Role != database.Role || expectation.SourceCommit != owner.SourceCommit
            || context.GetType().FullName != owner.ContextFullName
            || context.GetType().Assembly.GetName().Name != owner.AssemblyName)
        {
            throw new InvalidDataException("Pinned physical schema owner differs.");
        }

        OwnedBusinessSchema.VerifyQualifiedAssembly(context.GetType().Assembly, expectation.AssemblySha256);
        VerifyModel(context, tables);
        using var deadline = OwnedBusinessSchema.Deadline(database, cancellationToken);
        var token = deadline.Token;
        var connection = context.Database.GetDbConnection();
        // Non-opening admission precedes our cleanup authority. Reject foreign bindings untouched.
        if (context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL"
            || connection.GetType() != typeof(NpgsqlConnection))
        {
            throw new InvalidDataException("Exact genuine PostgreSQL owner connection required.");
        }
        var address = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        if (address.Host is not ("127.0.0.1" or "localhost" or "::1")
            || address.Port != database.Port || address.Database != database.Database
            || address.Username != "fixture_admin")
        {
            throw new InvalidDataException("Physical owner connection differs from its disposable lease.");
        }
        if (connection.State != ConnectionState.Closed || context.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Exclusive initially closed owning context required.");
        }
        DbDataReader? reader = null;
        DbCommand? command = null;
        DbCommand? setup = null;
        IDbContextTransaction? transaction = null;
        ExceptionDispatchInfo? operationFailure = null;
        try
        {
            // Protect every awaited gate/open attempt, including an unknown partially opened failure.
            await OwnedBusinessSchema.ObserveAsync(context, database, expectation.AssemblySha256, token);
            await context.Database.OpenConnectionAsync(token);
            transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            setup = context.Database.GetDbConnection().CreateCommand();
            setup.Transaction = transaction.GetDbTransaction();
            setup.CommandTimeout = 5;
            setup.CommandText = "SET TRANSACTION READ ONLY; SET LOCAL search_path TO pg_catalog";
            await setup.ExecuteNonQueryAsync(token);

            command = context.Database.GetDbConnection().CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandTimeout = 5;
            command.CommandText = CatalogQuery;
            AddParameter(command, "schemas", tables.Select(table => table.Schema).ToArray());
            AddParameter(command, "tables", tables.Select(table => table.Table).ToArray());
            var expectedColumns = tables.SelectMany(table => table.Columns.Select(column =>
                (Key: (table.Schema, table.Table, column.Name), Column: column)))
                .ToDictionary(item => item.Key, item => item.Column);
            var seen = new HashSet<(string, string, string)>();
            reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                token.ThrowIfCancellationRequested();
                if (checked((uint)reader.GetInt64(0)) != database.Oid
                    || reader.GetString(1) != database.Database || reader.GetString(2) != "fixture_admin"
                    || seen.Count >= MaximumColumns || reader.GetInt32(13) is < 180000 or >= 190000)
                {
                    throw new InvalidDataException("Physical column database identity or bound differs.");
                }
                var key = (reader.GetString(3), reader.GetString(4), reader.GetString(5));
                if (!seen.Add(key) || !expectedColumns.TryGetValue(key, out var column)
                    || reader.GetInt32(11) > MaximumExpressionBytes || !reader.GetBoolean(12))
                {
                    throw new InvalidDataException("Missing, duplicate, extra or oversized physical column evidence.");
                }
                var expression = reader.IsDBNull(10) ? null : reader.GetString(10);
                VerifyColumn(column, reader.GetString(6), reader.GetBoolean(7),
                    reader.GetString(8), reader.GetString(9), expression);
            }
            if (seen.Count != expectedColumns.Count)
            {
                throw new InvalidDataException("Owning physical column inventory is incomplete.");
            }
            // This is a read-only transaction; disposal rolls it back. No migration/DDL/write occurs here.
        }
        catch (Exception failure)
        {
            operationFailure = ExceptionDispatchInfo.Capture(failure);
        }
        finally
        {
            // Each acquired release is attempted independently; no release task is abandoned.
            var releases = new List<Func<Task>>();
            if (reader is not null)
            {
                releases.Add(() => reader.DisposeAsync().AsTask());
            }
            if (command is not null)
            {
                releases.Add(() => command.DisposeAsync().AsTask());
            }
            if (setup is not null)
            {
                releases.Add(() => setup.DisposeAsync().AsTask());
            }
            if (transaction is not null)
            {
                releases.Add(() => transaction.DisposeAsync().AsTask());
            }
            releases.Add(() => context.Database.CloseConnectionAsync());
            // Unknown EF gate/open failures can leave its internal open count unsettled.
            // Independently close the exact initially closed physical connection, then inspect it.
            releases.Add(() => connection.CloseAsync());
            releases.Add(() => connection.State == ConnectionState.Closed
                ? Task.CompletedTask
                : Task.FromException(new InvalidDataException("Physical connection did not close.")));
            await PhysicalSchemaRelease.FinishAsync(operationFailure, releases);
        }
        return new(database.Role, expectation.SourceCommit, expectation.AssemblySha256,
            expectation.OwnerExpectationSha256, tables.Length, tables.Sum(table => table.Columns.Count),
            true, true, true, true);
    }

    // A digest binds private owner expectations to the parent's independently qualified receipt.
    // Equality is NOT authentication: the parent must acquire the expected digest independently.
    public static string ComputeExpectationDigest(IReadOnlyList<PhysicalTableExpectation> tables)
    {
        // Public callers receive the same pre-allocation leaf validation as ObserveAsync.
        var canonical = SnapshotTables(tables).OrderBy(table => table.Schema, StringComparer.Ordinal)
            .ThenBy(table => table.Table, StringComparer.Ordinal)
            .Select(table => new PhysicalTableExpectation(table.Schema, table.Table,
                table.Columns.OrderBy(column => column.Name, StringComparer.Ordinal).ToArray())).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical)));
    }

    public static void VerifyColumn(PhysicalColumnExpectation expected, string storeType, bool notNull,
        string identityKind, string generatedKind, string? expression)
    {
        if (expected.PostgreSqlStoreType != storeType || expected.Nullable == notNull
            || expected.IdentityKind != identityKind || expected.GeneratedKind != generatedKind
            || expected.PostgreSqlExpression != expression)
        {
            throw new InvalidDataException("Physical owning column type, nullability, default or generation differs.");
        }
    }

    private static PhysicalTableExpectation[] Snapshot(QualifiedPhysicalSchemaExpectation expectation)
    {
        var tables = SnapshotTables(expectation.Tables);
        if (!IsHex(expectation.SourceCommit, 40) || !IsHex(expectation.AssemblySha256, 64)
            || !IsHex(expectation.OwnerExpectationSha256, 64)
            || ComputeExpectationDigest(tables) != expectation.OwnerExpectationSha256)
        {
            throw new InvalidDataException("Independently qualified physical expectation required.");
        }
        return tables;
    }

    private static PhysicalTableExpectation[] SnapshotTables(IReadOnlyList<PhysicalTableExpectation> input)
    {
        if (input.Count is < 1 or > MaximumTables
            || input.Any(table => table.Columns.Count is < 1 or > MaximumColumns)
            || input.Sum(table => (long)table.Columns.Count) > MaximumColumns)
        {
            throw new InvalidDataException("Finite owner table and column input required.");
        }
        var tables = input.Select(table => new PhysicalTableExpectation(table.Schema, table.Table,
            table.Columns.ToArray())).ToArray();
        if (tables.Select(table => (table.Schema, table.Table)).Distinct().Count() != tables.Length)
        {
            throw new InvalidDataException("Finite independently qualified physical expectation required.");
        }
        foreach (var table in tables)
        {
            if (!IsName(table.Schema) || !IsName(table.Table) || table.Columns.Count == 0
                || table.Columns.Select(column => column.Name).Distinct(StringComparer.Ordinal).Count() != table.Columns.Count)
            {
                throw new InvalidDataException("Unique complete owner table expectation required.");
            }
            foreach (var column in table.Columns)
            {
                if (!IsName(column.Name) || column.ModelStoreType.Length is < 1 or > 512
                    || column.PostgreSqlStoreType.Length is < 1 or > 512
                    || column.IdentityKind is not ("" or "a" or "d")
                    || column.GeneratedKind is not ("" or "s" or "v")
                    || (column.GeneratedKind != "" && (column.IdentityKind != "" || column.PostgreSqlExpression is null))
                    || (column.PostgreSqlExpression is not null
                        && (column.PostgreSqlExpression.Length > MaximumExpressionBytes
                            || System.Text.Encoding.UTF8.GetByteCount(column.PostgreSqlExpression) > MaximumExpressionBytes)))
                {
                    throw new InvalidDataException("Finite exact owner column expectation required.");
                }
            }
        }
        return tables;
    }

    private static void VerifyModel(DbContext context, PhysicalTableExpectation[] tables)
    {
        var modelTables = context.Model.GetRelationalModel().Tables.ToArray();
        if (modelTables.Length != tables.Length)
        {
            throw new InvalidDataException("Owning relational table inventory differs.");
        }
        foreach (var expected in tables)
        {
            var table = modelTables.SingleOrDefault(table => (table.Schema ?? "public") == expected.Schema && table.Name == expected.Table);
            if (table is null || table.Columns.Count() != expected.Columns.Count)
            {
                throw new InvalidDataException("Owning relational column inventory differs.");
            }
            foreach (var column in expected.Columns)
            {
                var actual = table.FindColumn(column.Name);
                if (actual is null || actual.StoreType != column.ModelStoreType || actual.IsNullable != column.Nullable)
                {
                    throw new InvalidDataException("Owner expectation differs from the exact owning relational model.");
                }
            }
        }
    }

    private static bool IsHex(string value, int length) => value.Length == length
        && value.All(character => "0123456789abcdef".Contains(character));

    private static bool IsName(string value) => value.Length is > 0 and <= 63 && !value.Contains('\0');

    private static void AddParameter(DbCommand command, string name, string[] value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private const string CatalogQuery = """
        SELECT d.oid::bigint, current_database(), current_user,
               n.nspname::text, c.relname::text, a.attname::text,
               pg_catalog.format_type(a.atttypid, a.atttypmod), a.attnotnull,
               a.attidentity::text, a.attgenerated::text,
               CASE WHEN COALESCE(octet_length(pg_catalog.pg_get_expr(ad.adbin, ad.adrelid, false)), 0) <= 8192
                    THEN pg_catalog.pg_get_expr(ad.adbin, ad.adrelid, false) ELSE NULL END,
               COALESCE(octet_length(pg_catalog.pg_get_expr(ad.adbin, ad.adrelid, false)), 0),
               NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint nc
                           WHERE nc.conrelid = c.oid AND nc.contype = 'n'
                             AND a.attnum = ANY(nc.conkey) AND (NOT nc.convalidated OR NOT nc.conenforced)),
               current_setting('server_version_num')::integer
        FROM pg_catalog.pg_database d
        CROSS JOIN unnest(@schemas::text[], @tables::text[]) AS wanted(schema_name, table_name)
        JOIN pg_catalog.pg_namespace n ON n.nspname = wanted.schema_name
        JOIN pg_catalog.pg_class c ON c.relnamespace = n.oid AND c.relname = wanted.table_name
        JOIN pg_catalog.pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
        LEFT JOIN pg_catalog.pg_attrdef ad ON ad.adrelid = c.oid AND ad.adnum = a.attnum
        WHERE d.datname = current_database() AND c.relkind IN ('r', 'p') AND c.relpersistence = 'p'
        ORDER BY n.nspname, c.relname, a.attnum
        LIMIT 16385
        """;
}
