namespace Legacy.Maliev.QuotationService.Tests.Infrastructure;

/// <summary>Test-only connection lifetime boundary for owned disposable fixture databases.</summary>
internal static class DisposablePostgresConnectionPolicy
{
    // A recycled loopback port is a new server lifetime, not the old pool's identity.
    internal static string Isolate(string connectionString) =>
        new Npgsql.NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;
}
