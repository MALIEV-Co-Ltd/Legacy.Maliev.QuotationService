using System.Text.Json;

namespace InvoiceCompletionProducerAcceptance;

/// <summary>Validated Accounting document coordinates; not proof of a stored external object.</summary>
public sealed class AccountingCompletionEvidence
{
    internal AccountingCompletionEvidence(string pdfSha256, string bucket, string objectName, JsonElement result)
    {
        PdfSha256 = pdfSha256;
        Bucket = bucket;
        ObjectName = objectName;
        Result = result;
    }

    /// <summary>Gets the exact uppercase digest retained in the validated Accounting phase.</summary>
    public string PdfSha256 { get; }

    /// <summary>Gets the bucket shared by the validated phase and durable result.</summary>
    public string Bucket { get; }

    /// <summary>Gets the object name shared by the validated phase and durable result.</summary>
    public string ObjectName { get; }

    /// <summary>Gets the cloned durable result, independent of the caller's document lifetime.</summary>
    public JsonElement Result { get; }
}
