using Npgsql;

namespace InvoiceCompletionProducerAcceptance;

/// <summary>Exact consumed PostgreSQL configuration contracts, inspected from owning registrations.</summary>
public static class ProducerDatabaseBindings
{
    private static readonly Dictionary<string, Dictionary<string, string>> Contracts = new(StringComparer.Ordinal)
    {
        ["Auth"] = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings__CustomerIdentity"] = "AUTH_CUSTOMER_IDENTITY",
            ["ConnectionStrings__EmployeeIdentity"] = "AUTH_EMPLOYEE_IDENTITY",
            ["ConnectionStrings__RefreshSessions"] = "AUTH_SESSIONS",
        },
        ["Accounting"] = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings__PaymentDbContext"] = "PAYMENT",
            ["ConnectionStrings__InvoiceDbContext"] = "INVOICE",
            ["ConnectionStrings__ReceiptDbContext"] = "RECEIPT",
        },
        ["Quotation"] = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings__QuotationDbContext"] = "QUOTATION",
            ["ConnectionStrings__QuotationRequestDbContext"] = "QUOTATION_REQUEST",
        },
        ["Order"] = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings__OrderDbContext"] = "ORDER",
            ["ConnectionStrings__OrderStatusDbContext"] = "ORDER_STATUS",
        },
        ["IAM"] = new(StringComparer.Ordinal)
        {
            ["ConnectionStrings__IamDbContext"] = "IAM",
        },
    };

    /// <summary>Returns an independent copy of the exact owner contract.</summary>
    public static Dictionary<string, string> Required(string owner) => Contracts.TryGetValue(owner, out var contract)
        ? new(contract, StringComparer.Ordinal) : throw new InvalidDataException("Unknown database owner.");

    /// <summary>All normal registered databases must be separately isolated, including unused sibling contexts.</summary>
    public static string[] Roles() => Contracts.Values.SelectMany(contract => contract.Values).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>Normal fixture launch admits only dotnet plus the absolute DLL; no configuration arguments.</summary>
    public static void ValidateLaunchArguments(IReadOnlyList<string> command, string executableDll, string actualExecutable)
    {
        if (!Path.IsPathFullyQualified(executableDll) || command.Count != 2
            || Path.GetFileName(command[0]) != "dotnet" || Path.GetFileName(actualExecutable) != "dotnet"
            || command[1] != Path.GetFullPath(executableDll))
            throw new InvalidDataException("Only normal dotnet absolute-DLL launch is admitted.");
    }

    /// <summary>Distinct roles require distinct physical databases, regardless of credentials or loopback spelling.</summary>
    public static void ValidateSeparateDatabases(IReadOnlyDictionary<string, string> isolated)
    {
        var roles = Roles();
        if (isolated.Count != roles.Length || !isolated.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(roles))
            throw new InvalidDataException("Exact isolated database role inventory required.");
        var backends = new HashSet<(string Host, int Port, string Database)>();
        foreach (var role in roles)
        {
            var connection = new NpgsqlConnectionStringBuilder(isolated[role]);
            if (connection.Host is not ("127.0.0.1" or "localhost" or "::1") || string.IsNullOrWhiteSpace(connection.Database))
                throw new InvalidDataException("Loopback isolated database required.");
            // All admitted loopback aliases identify the fixture's local server at this port.
            // PostgreSQL database names remain case-sensitive; credentials are not backend identity.
            if (!backends.Add(("loopback", connection.Port, connection.Database)))
                throw new InvalidDataException("Logical database roles share a physical backend.");
        }
    }

    /// <summary>Rejects dummy keys, missing identity stores, role substitution and extra owner keys.</summary>
    public static void Validate(string owner, IReadOnlyDictionary<string, string> supplied)
    {
        var required = Required(owner);
        if (supplied.Count != required.Count || required.Any(binding => !supplied.TryGetValue(binding.Key, out var role) || role != binding.Value))
            throw new InvalidDataException("Exact consumed database bindings required.");
    }

    /// <summary>Rechecks actual process values at the consumed keys, with no credential output.</summary>
    public static void ValidateEnvironment(string owner, IReadOnlyDictionary<string, string> supplied,
        IReadOnlyDictionary<string, string> environment, IReadOnlyDictionary<string, string> isolated)
    {
        Validate(owner, supplied);
        foreach (var binding in Required(owner))
        {
            var aliases = environment.Keys.Where(key => key.Replace(":", "__", StringComparison.Ordinal)
                .Equals(binding.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (aliases is not [var actualKey] || actualKey != binding.Key || !isolated.TryGetValue(binding.Value, out var expectedValue))
                throw new InvalidDataException("Consumed database key missing or ambiguous.");
            var expected = new NpgsqlConnectionStringBuilder(expectedValue);
            var actual = new NpgsqlConnectionStringBuilder(environment[actualKey]);
            if (actual.Host != expected.Host || actual.Port != expected.Port || actual.Database != expected.Database
                || actual.Username != expected.Username || actual.Password != expected.Password)
                throw new InvalidOperationException("Actual consumed database isolation mismatch.");
        }
    }
}
