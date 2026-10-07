using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinancialCompositionAdapter;

// Private acquisition input: never serialize connections, credentials or acquired identifiers.
// The parent must supply this from its observed exact Docker/PG ownership lease, not a config file.
public sealed record OwnedDatabaseExpectation(
    string Role, string LeaseSuffix, string Database, uint Oid, int Port, DateTimeOffset ExpiresUtc);

public sealed record MigrationAndTablePresenceObservation(string Role, string ContextType, string MigrationDigest, int MigrationCount, int MappedTableCount);

public static class OwnedBusinessSchema
{
    private static readonly string[] Roles =
    [
        "AUTH_CUSTOMER_IDENTITY", "AUTH_EMPLOYEE_IDENTITY", "AUTH_SESSIONS", "PAYMENT", "INVOICE", "RECEIPT",
        "QUOTATION", "QUOTATION_REQUEST", "ORDER", "ORDER_STATUS", "IAM", "FILE_DATABASE",
    ];

    // Does not run migrations or create marker tables. Normal owning startup must initialize real schemas.
    // Every expected migration ID must come from an independently sealed exact owning source graph.
    public static async Task<MigrationAndTablePresenceObservation> ObserveAsync(
        DbContext context, OwnedDatabaseExpectation expected, string qualifiedAssemblySha256,
        CancellationToken cancellationToken)
    {
        using var deadline = Deadline(expected, cancellationToken);
        var token = deadline.Token;
        var sourceMigrations = ValidateOwningType(context, expected.Role, qualifiedAssemblySha256).Migrations;
        await VerifyDatabaseAsync(context, expected, token);
        var declared = context.Database.GetMigrations().ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(token)).ToArray();
        if (sourceMigrations.Length == 0 || !declared.SequenceEqual(sourceMigrations, StringComparer.Ordinal)
            || !applied.SequenceEqual(declared, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Owning migration inventory or applied business schema differs.");
        }

        var tables = context.Model.GetEntityTypes().Where(entity => entity.GetTableName() is not null)
            .Select(entity => (Schema: entity.GetSchema() ?? "public", Table: entity.GetTableName()!)).Distinct().ToArray();
        if (tables.Length == 0)
        {
            throw new InvalidDataException("Owning context has no business tables.");
        }

        await context.Database.OpenConnectionAsync(token);
        try
        {
            foreach (var table in tables)
            {
                await using var command = context.Database.GetDbConnection().CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@table AND c.relkind IN ('r','p'))";
                AddParameter(command, "schema", table.Schema);
                AddParameter(command, "table", table.Table);
                if (await command.ExecuteScalarAsync(token) is not true)
                {
                    throw new InvalidDataException("An owning business table is physically absent.");
                }
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', applied) + "\n")));
        return new(expected.Role, context.GetType().FullName!, digest, applied.Length, tables.Length);
    }

    public static async Task<IReadOnlyList<MigrationAndTablePresenceObservation>> ObserveTwelveAsync(
        IReadOnlyList<(DbContext Context, OwnedDatabaseExpectation Expected, string QualifiedAssemblySha256)> bindings,
        CancellationToken cancellationToken)
    {
        if (bindings.Count != Roles.Length || !bindings.Select(binding => binding.Expected.Role).ToHashSet(StringComparer.Ordinal).SetEquals(Roles)
            || bindings.Select(binding => binding.Expected.Database).Distinct(StringComparer.Ordinal).Count() != Roles.Length
            || bindings.Select(binding => (binding.Expected.Port, binding.Expected.Oid)).Distinct().Count() != Roles.Length
            || bindings.Select(binding => binding.Expected.Port).Distinct().Count() != 1
            || bindings.Select(binding => binding.Expected.LeaseSuffix).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new InvalidDataException("Exact twelve independent owned physical databases required.");
        }

        var observations = new List<MigrationAndTablePresenceObservation>();
        foreach (var binding in bindings)
        {
            observations.Add(await ObserveAsync(binding.Context, binding.Expected, binding.QualifiedAssemblySha256, cancellationToken));
        }
        return observations.AsReadOnly();
    }

    internal static CancellationTokenSource Deadline(OwnedDatabaseExpectation expected, CancellationToken cancellationToken)
    {
        var remaining = expected.ExpiresUtc - DateTimeOffset.UtcNow;
        if (!Roles.Contains(expected.Role, StringComparer.Ordinal) || expected.Oid == 0 || expected.Port is < 1 or > 65535
            || expected.LeaseSuffix.Length != 32 || expected.LeaseSuffix.Any(character => !"0123456789abcdef".Contains(character))
            || expected.Database != $"c821_{expected.LeaseSuffix}_{expected.Role.ToLowerInvariant()}"
            || remaining <= TimeSpan.Zero || remaining > TimeSpan.FromMinutes(30))
        {
            throw new InvalidDataException("Finite exact disposable database ownership input required.");
        }
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30));
        return deadline;
    }

    internal static async Task VerifyDatabaseAsync(DbContext context, OwnedDatabaseExpectation expected, CancellationToken token)
    {
        if (context.Database.ProviderName != "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            throw new InvalidDataException("Genuine PostgreSQL owning context required.");
        }
        var actual = new NpgsqlConnectionStringBuilder(context.Database.GetDbConnection().ConnectionString);
        if (actual.Host is not ("127.0.0.1" or "localhost" or "::1") || actual.Port != expected.Port
            || actual.Database != expected.Database || actual.Username != "fixture_admin")
        {
            throw new InvalidDataException("Runtime context is not bound to the exact loopback disposable database.");
        }
        if (context.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException("Composition observation requires an exclusive quiescent runtime scope.");
        }

        await context.Database.OpenConnectionAsync(token);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = "SELECT d.oid::bigint, current_database(), current_user FROM pg_catalog.pg_database d WHERE d.datname=current_database()";
            await using var reader = await command.ExecuteReaderAsync(token);
            if (!await reader.ReadAsync(token) || checked((uint)reader.GetInt64(0)) != expected.Oid
                || reader.GetString(1) != expected.Database || reader.GetString(2) != "fixture_admin" || await reader.ReadAsync(token))
            {
                throw new InvalidDataException("Observed physical database identity differs from its parent ownership lease.");
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static PinnedBusinessSchema ValidateOwningType(DbContext context, string role, string qualifiedAssemblySha256)
    {
        if (!PinnedBusinessSchemas.Contracts.TryGetValue(role, out var contract)
            || context.GetType().FullName != contract.ContextFullName || context.GetType().Assembly.GetName().Name != contract.AssemblyName
            || qualifiedAssemblySha256.Length != 64 || qualifiedAssemblySha256.Any(character => !"0123456789abcdef".Contains(character)))
        {
            throw new InvalidDataException("Exact source-owned role/context and separately qualified assembly digest required.");
        }
        VerifyQualifiedAssembly(context.GetType().Assembly, qualifiedAssemblySha256);
        return contract;
    }

    internal static void VerifyQualifiedAssembly(System.Reflection.Assembly assembly, string qualifiedAssemblySha256)
    {
        if (qualifiedAssemblySha256.Length != 64 || qualifiedAssemblySha256.Any(character => !"0123456789abcdef".Contains(character)))
        {
            throw new InvalidDataException("Parent qualified exact-source assembly digest required.");
        }
        var path = assembly.Location;
        if (string.IsNullOrEmpty(path) || new FileInfo(path).Length is <= 0 or > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("Finite separately qualified physical owner assembly required.");
        }
        using var stream = File.OpenRead(path);
        if (Convert.ToHexStringLower(SHA256.HashData(stream)) != qualifiedAssemblySha256)
        {
            throw new InvalidDataException("Owning runtime assembly differs from the parent's qualified exact-source build receipt.");
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
