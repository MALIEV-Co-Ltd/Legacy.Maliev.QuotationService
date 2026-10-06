using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace InvoiceCompletionProducerAcceptance;

/// <summary>Successor probe for the actual admitted Accounting complete consumer; never a persistence substitute.</summary>
public static class AccountingFullCompletionProbe
{
    /// <summary>Called only after exact producer process/database and external fixture admission.</summary>
    public static async Task<AccountingCompletionEvidence> RunAsync(HttpClient accounting, string intranetAccessToken, Func<CancellationToken, Task<string>> freshCapability,
        JsonElement originalIntent, int quotationId, int invoiceId, Guid operationId, string expectedFinancialBinding, string expectedDecisionOrderVersion,
        int expectedTotalOrders, string invoiceConnection,
        Func<CancellationToken, Task<string>> financialOrderFingerprint, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || accounting.BaseAddress is not { Scheme: "https", IsLoopback: true }
            || operationId == Guid.Empty || quotationId <= 0 || invoiceId <= 0 || expectedTotalOrders < 0
            || expectedFinancialBinding.Length != 64
            || expectedFinancialBinding.Any(character => character is not (>= '0' and <= '9' or >= 'A' and <= 'F'))
            || string.IsNullOrWhiteSpace(expectedDecisionOrderVersion)
            || !originalIntent.TryGetProperty("SendEmail", out var sendEmail) || sendEmail.ValueKind != JsonValueKind.False)
            throw new InvalidOperationException("Admitted hosted no-send scenario required.");
        var database = new NpgsqlConnectionStringBuilder(invoiceConnection);
        if (database.Host is not ("127.0.0.1" or "localhost" or "::1") || database.Database is null
            || !database.Database.StartsWith("c821_", StringComparison.Ordinal)) throw new InvalidDataException();
        database.Pooling = false;
        database.Timeout = 10;
        database.CommandTimeout = 15;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        cancellationToken = deadline.Token;
        var before = await financialOrderFingerprint(cancellationToken).WaitAsync(cancellationToken);
        using (var request = Request(await freshCapability(cancellationToken).WaitAsync(cancellationToken)))
        using (var lostAcknowledgment = await accounting.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            // Discard first consumer acknowledgement; only committed admission and file rows establish success.
        }
        var retained = await ReadCompleted();
        var stable = await Fingerprint();
        using var replay = Request(await freshCapability(cancellationToken).WaitAsync(cancellationToken));
        using var response = await accounting.SendAsync(replay, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException();
        using var body = JsonDocument.Parse(await Bounded(response.Content, cancellationToken));
        if (!AccountingCompletionReceipt.SameResult(body.RootElement, retained.Result, invoiceId) || await Fingerprint() != stable
            || await financialOrderFingerprint(cancellationToken).WaitAsync(cancellationToken) != before) throw new InvalidDataException("Completed consumer replay differs.");
        return retained;

        HttpRequestMessage Request(string raw)
        {
            if (raw.Length is < 1 or > 16384 || raw.Any(char.IsWhiteSpace)) throw new InvalidDataException();
            var request = new HttpRequestMessage(HttpMethod.Post, $"/invoices/from-quotation/{quotationId}/complete")
            {
                Content = JsonContent.Create(originalIntent),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", intranetAccessToken);
            request.Headers.Add("Idempotency-Key", operationId.ToString("D"));
            request.Headers.Add("X-Maliev-Quotation-Invoice-Capability", raw);
            return request;
        }

        async Task<AccountingCompletionEvidence> ReadCompleted()
        {
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT json_build_object('Phase', "EmployeeCompletionJson"::json, 'Result', "ResultJson"::json)::text
                FROM "InvoiceCreationAdmission"
                WHERE "OperationID"=@operation AND "QuotationID"=@quotation AND "State"='Completed'
                 AND "EmployeeCompletionJson"::jsonb->>'State'='Completed'
                 AND ("EmployeeCompletionJson"::jsonb->>'InvoiceId')::int=@invoice
                """, connection) { CommandTimeout = 15 };
            command.Parameters.AddWithValue("operation", operationId);
            command.Parameters.AddWithValue("quotation", quotationId);
            command.Parameters.AddWithValue("invoice", invoiceId);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string ?? throw new InvalidDataException();
            if (json.Length > 65536) throw new InvalidDataException();
            using var parsed = JsonDocument.Parse(json);
            var result = AccountingCompletionReceipt.ValidateEvidence(parsed.RootElement, operationId, quotationId, invoiceId,
                expectedFinancialBinding, expectedDecisionOrderVersion, expectedTotalOrders);
            await using var linked = new NpgsqlCommand("""
                SELECT COUNT(*)::int,
                  (COUNT(*) FILTER (WHERE "Bucket"=@bucket AND "ObjectName"=@object))::int
                FROM "InvoiceFile" WHERE "InvoiceID"=@invoice
                """, connection) { CommandTimeout = 15 };
            linked.Parameters.AddWithValue("invoice", invoiceId);
            linked.Parameters.AddWithValue("bucket", result.Bucket);
            linked.Parameters.AddWithValue("object", result.ObjectName);
            await using var linkCounts = await linked.ExecuteReaderAsync(cancellationToken);
            if (!await linkCounts.ReadAsync(cancellationToken)) throw new InvalidDataException();
            AccountingCompletionReceipt.RequireSingleInvoiceFile(linkCounts.GetInt32(0), linkCounts.GetInt32(1));
            return result;
        }

        async Task<string> Fingerprint()
        {
            _ = await ReadCompleted();
            await using var connection = new NpgsqlConnection(database.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                SELECT json_build_array(
                  (SELECT row_to_json(a) FROM "InvoiceCreationAdmission" a WHERE "OperationID"=@operation),
                  (SELECT COALESCE(json_agg(f ORDER BY "ID"),'[]'::json) FROM "InvoiceFile" f WHERE "InvoiceID"=@invoice)
                )::text
                """, connection) { CommandTimeout = 15 };
            command.Parameters.AddWithValue("operation", operationId);
            command.Parameters.AddWithValue("invoice", invoiceId);
            var json = await command.ExecuteScalarAsync(cancellationToken) as string ?? throw new InvalidDataException();
            if (json.Length > 262144) throw new InvalidDataException();
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }
    }

    private static async Task<byte[]> Bounded(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var memory = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            if (memory.Length + count > 65536) throw new InvalidDataException();
            memory.Write(buffer, 0, count);
        }
        return memory.ToArray();
    }
}
