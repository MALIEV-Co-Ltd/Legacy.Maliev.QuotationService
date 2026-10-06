using System.Text.Json;
using System.Text.Json.Nodes;
using InvoiceCompletionProducerAcceptance;
using Xunit;

namespace Legacy.Maliev.QuotationService.Tests.Tools;

/// <summary>Future hosted controls; synthetic JSON is confined to pure receipt validation.</summary>
public sealed class AccountingCompletionReceiptTests
{
    private static readonly Guid Operation = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private const string Binding = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Version = "2026-10-06T08:00:00.0000000Z";

    [Fact]
    public void ExactRetainedNoSendPhaseMatchesVerifiedOwningReceipt()
    {
        var value = Baseline();
        var result = Validate(value);
        Assert.Equal(19, result.GetProperty("InvoiceId").GetInt32());
        Assert.Equal("invoices/19/owned.pdf", result.GetProperty("StoredFile").GetProperty("ObjectName").GetString());
    }

    [Fact]
    public void HttpNullableOmissionMatchesDurableResult()
    {
        var retained = Validate(Baseline());
        var http = JsonNode.Parse(retained.GetRawText())!.AsObject();
        Assert.True(http.Remove("ProviderMessageId"));
        Assert.True(AccountingCompletionReceipt.SameResult(Element(http), retained, 19));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("operation")]
    [InlineData("quotation")]
    [InlineData("invoice")]
    [InlineData("binding")]
    [InlineData("decision")]
    [InlineData("membership")]
    [InlineData("incomplete")]
    [InlineData("digest")]
    [InlineData("missing-phase-field")]
    [InlineData("extra-phase-field")]
    [InlineData("wrong-field-type")]
    [InlineData("phase-file-mismatch")]
    [InlineData("wrong-bucket")]
    [InlineData("wrong-object-prefix")]
    [InlineData("phase-result-mismatch")]
    [InlineData("requested-email")]
    [InlineData("provider-message")]
    [InlineData("missing-durable-provider")]
    public void RetainedPhaseMismatchIsRejected(string mutation)
    {
        var envelope = Baseline();
        var phase = envelope["Phase"]!.AsObject();
        var result = envelope["Result"]!.AsObject();
        switch (mutation)
        {
            case "version": phase["ContractVersion"] = 2; break;
            case "operation": phase["OperationId"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"; break;
            case "quotation": phase["QuotationId"] = 8; break;
            case "invoice": phase["InvoiceId"] = 20; break;
            case "binding": phase["FinancialBinding"] = new string('B', 64); break;
            case "decision": phase["DecisionOrderVersion"] = "2026-10-06T09:00:00.0000000Z"; break;
            case "membership": phase["TotalOrders"] = 3; break;
            case "incomplete": phase["State"] = "DocumentReady"; break;
            case "digest": phase["PdfSha256"] = new string('a', 64); break;
            case "missing-phase-field": Assert.True(phase.Remove("PdfSha256")); break;
            case "extra-phase-field": phase["Unexpected"] = true; break;
            case "wrong-field-type": phase["InvoiceId"] = "19"; break;
            case "phase-file-mismatch": phase["StoredFile"]!["ObjectName"] = "invoices/19/different.pdf"; break;
            case "wrong-bucket": result["StoredFile"]!["Bucket"] = "different"; break;
            case "wrong-object-prefix": result["StoredFile"]!["ObjectName"] = "invoices/20/owned.pdf"; break;
            case "phase-result-mismatch": phase["Result"]!["StoredFile"]!["ObjectName"] = "invoices/19/different.pdf"; break;
            case "requested-email": result["EmailState"] = 1; break;
            case "provider-message": result["ProviderMessageId"] = "unexpected-provider"; break;
            case "missing-durable-provider": Assert.True(result.Remove("ProviderMessageId")); break;
            default: throw new InvalidOperationException();
        }
        Assert.Throws<InvalidDataException>(() => Validate(envelope));
    }

    [Theory]
    [InlineData("\"ContractVersion\":1", "\"ContractVersion\":1,\"ContractVersion\":1")]
    [InlineData("\"Bucket\":\"maliev.com\"", "\"Bucket\":\"maliev.com\",\"Bucket\":\"maliev.com\"")]
    public void DuplicatePhaseOrFileFieldsAreRejected(string original, string duplicate)
    {
        using var value = JsonDocument.Parse(Baseline().ToJsonString().Replace(original, duplicate, StringComparison.Ordinal));
        Assert.Throws<InvalidDataException>(() => AccountingCompletionReceipt.Validate(value.RootElement, Operation, 7, 19, Binding, Version, 2));
    }

    [Fact]
    public void AlteredHttpReplayIsRejected()
    {
        var result = Validate(Baseline());
        var replay = JsonNode.Parse(result.GetRawText())!.AsObject();
        replay["StoredFile"]!["ObjectName"] = "invoices/19/different.pdf";
        Assert.False(AccountingCompletionReceipt.SameResult(Element(replay), result, 19));
    }

    private static JsonElement Validate(JsonObject envelope) =>
        AccountingCompletionReceipt.Validate(Element(envelope), Operation, 7, 19, Binding, Version, 2);

    private static JsonElement Element(JsonNode value)
    {
        using var document = JsonDocument.Parse(value.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonObject Baseline()
    {
        var file = new JsonObject { ["Bucket"] = "maliev.com", ["ObjectName"] = "invoices/19/owned.pdf" };
        var result = new JsonObject
        {
            ["InvoiceId"] = 19,
            ["State"] = 0,
            ["EmailState"] = 0,
            ["ProviderMessageId"] = null,
            ["StoredFile"] = file.DeepClone(),
        };
        var phase = new JsonObject
        {
            ["ContractVersion"] = 1,
            ["OperationId"] = Operation.ToString("D"),
            ["QuotationId"] = 7,
            ["InvoiceId"] = 19,
            ["FinancialBinding"] = Binding,
            ["DecisionOrderVersion"] = Version,
            ["TotalOrders"] = 2,
            ["State"] = "Completed",
            ["PdfSha256"] = Binding,
            ["StoredFile"] = file,
            ["Result"] = result.DeepClone(),
        };
        return new JsonObject { ["Phase"] = phase, ["Result"] = result };
    }
}
