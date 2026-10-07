using System.Text.Json;

namespace InvoiceCompletionProducerAcceptance;

/// <summary>Pure validation shared by the real SQL readback and future hosted negative controls.</summary>
public static class AccountingCompletionReceipt
{
    /// <summary>Bind the retained Accounting phase and result to the actual verified Quotation receipt.</summary>
    public static JsonElement Validate(JsonElement envelope, Guid operationId, int quotationId, int invoiceId,
        string financialBinding, string decisionOrderVersion, int totalOrders)
        => ValidateEvidence(envelope, operationId, quotationId, invoiceId, financialBinding, decisionOrderVersion, totalOrders).Result;

    /// <summary>Preserves validated document identity for future independent object evidence.</summary>
    public static AccountingCompletionEvidence ValidateEvidence(JsonElement envelope, Guid operationId, int quotationId, int invoiceId,
        string financialBinding, string decisionOrderVersion, int totalOrders)
    {
        try
        {
            Exact(envelope, ["Phase", "Result"]);
            var phase = envelope.GetProperty("Phase");
            var result = envelope.GetProperty("Result");
            Exact(phase, ["ContractVersion", "OperationId", "QuotationId", "InvoiceId", "FinancialBinding",
                "DecisionOrderVersion", "TotalOrders", "State", "PdfSha256", "StoredFile", "Result"]);
            Exact(result, ["InvoiceId", "State", "EmailState", "ProviderMessageId", "StoredFile"]);
            Exact(phase.GetProperty("Result"), ["InvoiceId", "State", "EmailState", "ProviderMessageId", "StoredFile"]);
            if (phase.GetProperty("ContractVersion").GetInt32() != 1
                || phase.GetProperty("OperationId").GetString() != operationId.ToString("D")
                || phase.GetProperty("QuotationId").GetInt32() != quotationId
                || phase.GetProperty("InvoiceId").GetInt32() != invoiceId
                || phase.GetProperty("FinancialBinding").GetString() != financialBinding
                || phase.GetProperty("DecisionOrderVersion").GetString() != decisionOrderVersion
                || phase.GetProperty("TotalOrders").GetInt32() != totalOrders
                || phase.GetProperty("State").GetString() != "Completed"
                || !SameResult(result, result, invoiceId)
                || !SameResult(phase.GetProperty("Result"), result, invoiceId)) throw new InvalidDataException();
            var digest = phase.GetProperty("PdfSha256").GetString();
            if (digest is not { Length: 64 } || digest.Any(value => value is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
                throw new InvalidDataException();
            var phaseFile = phase.GetProperty("StoredFile");
            var resultFile = result.GetProperty("StoredFile");
            Exact(phaseFile, ["Bucket", "ObjectName"]);
            if (phaseFile.GetProperty("Bucket").GetString() != resultFile.GetProperty("Bucket").GetString()
                || phaseFile.GetProperty("ObjectName").GetString() != resultFile.GetProperty("ObjectName").GetString())
                throw new InvalidDataException();
            return new AccountingCompletionEvidence(digest!, phaseFile.GetProperty("Bucket").GetString()!,
                phaseFile.GetProperty("ObjectName").GetString()!, result.Clone());
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new InvalidDataException("Retained completion is malformed.");
        }
    }

    /// <summary>Requires both the entire invoice link set and the expected identity to be unique.</summary>
    public static void RequireSingleInvoiceFile(int totalLinks, int matchingLinks)
    {
        if (totalLinks != 1 || matchingLinks != 1)
            throw new InvalidDataException("Invoice file linkage is absent, ambiguous or mismatched.");
    }

    /// <summary>HTTP may omit null ProviderMessageId; retained SQL shape is validated separately.</summary>
    public static bool SameResult(JsonElement actual, JsonElement expected, int invoiceId)
    {
        try
        {
            string[] fields = ["InvoiceId", "State", "EmailState", "ProviderMessageId", "StoredFile"];
            if (actual.ValueKind != JsonValueKind.Object) return false;
            var names = actual.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Any(name => !fields.Contains(name, StringComparer.Ordinal))
                || names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || fields.Where(name => name != "ProviderMessageId").Any(name => !actual.TryGetProperty(name, out _))) return false;
            if (actual.GetProperty("InvoiceId").GetInt32() != invoiceId || actual.GetProperty("State").GetInt32() != 0
                || actual.GetProperty("EmailState").GetInt32() != 0
                || actual.TryGetProperty("ProviderMessageId", out var provider) && provider.ValueKind != JsonValueKind.Null) return false;
            var file = actual.GetProperty("StoredFile");
            var expectedFile = expected.GetProperty("StoredFile");
            Exact(file, ["Bucket", "ObjectName"]);
            Exact(expectedFile, ["Bucket", "ObjectName"]);
            return file.GetProperty("Bucket").GetString() == "maliev.com"
                && file.GetProperty("Bucket").GetString() == expectedFile.GetProperty("Bucket").GetString()
                && file.GetProperty("ObjectName").GetString() is { Length: <= 1024 } name
                && name.Length > $"invoices/{invoiceId}/".Length
                && name.StartsWith($"invoices/{invoiceId}/", StringComparison.Ordinal)
                && name == expectedFile.GetProperty("ObjectName").GetString();
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException or InvalidDataException)
        {
            return false;
        }
    }

    private static void Exact(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != fields.Length || names.Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Any(field => !names.Contains(field, StringComparer.Ordinal))) throw new InvalidDataException();
    }
}
