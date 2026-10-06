using System.Text.Json;
using InvoiceCompletionProducerAcceptance;
using Xunit;

namespace Legacy.Maliev.QuotationService.Tests.Tools;

/// <summary>Pure typed-evidence controls; no external object or database execution is implied.</summary>
public sealed class AccountingCompletionEvidenceTests
{
    private static readonly Guid Operation = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly string Binding = new('A', 64);
    private static readonly string Digest = new('C', 64);
    private const string Version = "2026-10-06T08:00:00.0000000Z";

    [Fact]
    public void TypedEvidencePreservesExactDocumentDigestAndCoordinates()
    {
        var evidence = Validate(Baseline());
        Assert.Equal(Digest, evidence.PdfSha256);
        Assert.NotEqual(Binding, evidence.PdfSha256);
        Assert.Equal("maliev.com", evidence.Bucket);
        Assert.Equal("invoices/19/owned.pdf", evidence.ObjectName);
        Assert.Equal(evidence.Bucket, evidence.Result.GetProperty("StoredFile").GetProperty("Bucket").GetString());
        Assert.Equal(evidence.ObjectName, evidence.Result.GetProperty("StoredFile").GetProperty("ObjectName").GetString());
    }

    [Fact]
    public void TypedEvidenceSurvivesSourceDocumentDisposal()
    {
        AccountingCompletionEvidence evidence;
        using (var document = JsonDocument.Parse(Baseline().GetRawText()))
        {
            evidence = Validate(document.RootElement);
        }
        Assert.Equal(19, evidence.Result.GetProperty("InvoiceId").GetInt32());
        Assert.Equal(Digest, evidence.PdfSha256);
        Assert.Equal("invoices/19/owned.pdf", evidence.ObjectName);
    }

    [Fact]
    public void OriginalValidateReturnsIdenticalDurableResult()
    {
        var envelope = Baseline();
        var original = AccountingCompletionReceipt.Validate(envelope, Operation, 7, 19, Binding, Version, 2);
        Assert.Equal(original.GetRawText(), Validate(envelope).Result.GetRawText());
    }

    [Fact]
    public void ExactlyOneTotalAndMatchingInvoiceFileIsAccepted()
    {
        AccountingCompletionReceipt.RequireSingleInvoiceFile(1, 1);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 1)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    [InlineData(-1, 1)]
    public void MissingExtraMismatchedOrInvalidLinkCountsAreRejected(int total, int matching)
    {
        Assert.Throws<InvalidDataException>(() => AccountingCompletionReceipt.RequireSingleInvoiceFile(total, matching));
    }

    private static AccountingCompletionEvidence Validate(JsonElement envelope) =>
        AccountingCompletionReceipt.ValidateEvidence(envelope, Operation, 7, 19, Binding, Version, 2);

    private static JsonElement Baseline()
    {
        var file = new { Bucket = "maliev.com", ObjectName = "invoices/19/owned.pdf" };
        var result = new { InvoiceId = 19, State = 0, EmailState = 0, ProviderMessageId = (string?)null, StoredFile = file };
        return JsonSerializer.SerializeToElement(new
        {
            Phase = new
            {
                ContractVersion = 1,
                OperationId = Operation.ToString("D"),
                QuotationId = 7,
                InvoiceId = 19,
                FinancialBinding = Binding,
                DecisionOrderVersion = Version,
                TotalOrders = 2,
                State = "Completed",
                PdfSha256 = Digest,
                StoredFile = file,
                Result = result,
            },
            Result = result,
        });
    }
}
