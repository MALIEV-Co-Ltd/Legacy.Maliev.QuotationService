using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace InvoiceCompletionProducerAcceptance;

/// <summary>Read-only prerequisites for root-owned companion hosts; never a storage/provider admission.</summary>
public static class CompanionLauncherAdmission
{
    /// <summary>Re-observe an already source-admitted real companion process without starting or stopping it.</summary>
    public static async Task VerifyProcessAsync(string owner, int processId, DateTimeOffset startedUtc,
        string executableDll, string expectedDllSha256, string runId, DateTimeOffset expiresUtc,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || owner is not ("Document" or "File" or "Notification") || processId <= 0
            || !Path.IsPathFullyQualified(executableDll)
            || expectedDllSha256.Length != 64
            || expectedDllSha256.Any(value => value is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
            || expiresUtc <= DateTimeOffset.UtcNow || expiresUtc > DateTimeOffset.UtcNow.AddMinutes(30))
            throw new InvalidDataException("Finite source-admitted companion required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = Process.GetProcessById(processId);
        Observe(process, startedUtc);
        var command = Encoding.UTF8.GetString(await ReadBounded($"/proc/{processId}/cmdline", 65536, deadline.Token))
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
        ProducerDatabaseBindings.ValidateLaunchArguments(command, executableDll, process.MainModule?.FileName ?? "");
        var environment = Decode(await ReadBounded($"/proc/{processId}/environ", 262144, deadline.Token));
        RequireCompanionEnvironment(owner, environment);
        if (environment.GetValueOrDefault("C821_FIXTURE_RUN_ID") != runId
            || !DateTimeOffset.TryParse(environment.GetValueOrDefault("C821_FIXTURE_EXPIRES_UTC"),
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var actualExpiry) || actualExpiry != expiresUtc)
            throw new InvalidDataException("Companion run identity or lease differs.");
        await using var dll = new FileStream(executableDll, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        if (Convert.ToHexString(await SHA256.HashDataAsync(dll, deadline.Token)) != expectedDllSha256)
            throw new InvalidDataException("Companion executable differs.");
        if (owner == "Notification") RequireExact(environment, "Notifications__DeliveryIntentsEnabled", "false");
        Observe(process, startedUtc);
        if (expiresUtc <= DateTimeOffset.UtcNow) throw new InvalidDataException("Companion lease expired.");
    }

    /// <summary>Checks the twelfth physical backend and actual consumed File key; no credential is emitted.</summary>
    public static void ValidateFileDatabase(IReadOnlyDictionary<string, string> core,
        string fileConnection, IReadOnlyDictionary<string, string> fileEnvironment, string runId)
    {
        ProducerDatabaseBindings.ValidateSeparateDatabases(core);
        if (!runId.StartsWith("c821-", StringComparison.Ordinal)
            || !Guid.TryParseExact(runId[5..], "D", out var run) || run == Guid.Empty
            || runId != "c821-" + run.ToString("D")) throw new InvalidDataException();
        var expected = new NpgsqlConnectionStringBuilder(fileConnection);
        if (expected.Host is not ("127.0.0.1" or "localhost" or "::1") || expected.Database is null
            || !expected.Database.StartsWith("c821_", StringComparison.Ordinal)
            || !expected.Database.Contains(run.ToString("N"), StringComparison.Ordinal)) throw new InvalidDataException();
        const string consumedKey = "ConnectionStrings__FileDbContext";
        var actual = new NpgsqlConnectionStringBuilder(RequireExact(fileEnvironment, consumedKey));
        if (actual.Host != expected.Host || actual.Port != expected.Port || actual.Database != expected.Database
            || actual.Username != expected.Username || actual.Password != expected.Password)
            throw new InvalidDataException("Actual consumed File database differs.");
        foreach (var value in core.Values)
        {
            var sibling = new NpgsqlConnectionStringBuilder(value);
            // All admitted loopback aliases at a port refer to the same local PostgreSQL server.
            if (sibling.Port == expected.Port && sibling.Database == expected.Database)
                throw new InvalidDataException("File shares a physical core backend.");
        }
    }

    /// <summary>Actual File version readback only; schemas and data are never changed.</summary>
    public static async Task RequireFilePostgres18Async(string fileConnection, CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnectionStringBuilder(fileConnection)
        {
            Pooling = false,
            Timeout = 10,
            CommandTimeout = 15,
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        await using var database = new NpgsqlConnection(connection.ConnectionString);
        await database.OpenAsync(deadline.Token);
        await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::int", database) { CommandTimeout = 15 };
        var version = (int)(await command.ExecuteScalarAsync(deadline.Token) ?? 0);
        if (version is < 180000 or >= 190000) throw new InvalidDataException("PostgreSQL18 required.");
    }

    /// <summary>Checks process environment only; File still requires independent hosted storage/scanner admission.</summary>
    public static void RequireCompanionEnvironment(string owner, IReadOnlyDictionary<string, string> environment)
    {
        var expected = owner switch
        {
            "File" => "HostedFinancialCompletionAcceptance",
            "Document" or "Notification" => "Production",
            _ => throw new InvalidDataException("Unknown companion owner."),
        };
        RequireExact(environment, "ASPNETCORE_ENVIRONMENT", expected);
        if (environment.Keys.Any(key => key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)))
            RequireExact(environment, "DOTNET_ENVIRONMENT", expected);
    }

    /// <summary>All supplied framework environment selectors must agree on Production.</summary>
    public static void RequireProductionEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        RequireExact(environment, "ASPNETCORE_ENVIRONMENT", "Production");
        if (environment.Keys.Any(key => key.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)))
            RequireExact(environment, "DOTNET_ENVIRONMENT", "Production");
    }

    /// <summary>Reject consumed-key substitution and case/colon aliases before reading a value.</summary>
    public static string RequireExact(IReadOnlyDictionary<string, string> environment, string key, string? expected = null)
    {
        var aliases = environment.Keys.Where(candidate => candidate.Replace(":", "__", StringComparison.Ordinal)
            .Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (aliases is not [var actual] || actual != key || expected is not null && environment[actual] != expected)
            throw new InvalidDataException("Consumed companion key missing or ambiguous.");
        return environment[actual];
    }

    private static void Observe(Process process, DateTimeOffset startedUtc)
    {
        process.Refresh();
        if (process.HasExited || Math.Abs((process.StartTime.ToUniversalTime() - startedUtc.UtcDateTime).TotalMilliseconds) > 1)
            throw new InvalidDataException("Companion process identity changed.");
    }

    private static Dictionary<string, string> Decode(byte[] value) => Encoding.UTF8.GetString(value)
        .Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(item => item.Split('=', 2))
        .ToDictionary(item => item[0], item => item.Length == 2 ? item[1] : "", StringComparer.Ordinal);

    private static async Task<byte[]> ReadBounded(string path, int maximum, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) return output.ToArray();
            if (output.Length + count > maximum) throw new InvalidDataException();
            output.Write(buffer, 0, count);
        }
    }
}
