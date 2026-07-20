namespace Legacy.Maliev.QuotationService.Domain;

/// <summary>Legacy quotation financial record.</summary>
public sealed class Quotation
{
    public int Id { get; set; }
    public int? CustomerId { get; set; }
    public int? EmployeeId { get; set; }
    public int? InvoiceId { get; set; }
    public int Period { get; set; }
    public DateTime ExpirationDate { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Vat { get; set; }
    public decimal Total { get; set; }
    public decimal? WithholdingTax { get; set; }
    public decimal? QuotedAmount { get; private set; }
    public int CurrencyId { get; set; }
    public string? Comment { get; set; }
    public string? Fob { get; set; }
    public string? ShippedVia { get; set; }
    public string? Terms { get; set; }
    public bool? Accepted { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public ICollection<QuotationOrderItem> OrderItems { get; } = [];
    public ICollection<QuotationFile> Files { get; } = [];
    public ICollection<QuotationOrderLink> Orders { get; } = [];
}

/// <summary>Legacy quotation line.</summary>
public sealed class QuotationOrderItem
{
    public int Id { get; set; }
    public int QuotationId { get; set; }
    public int? OrderId { get; set; }
    public string? Description { get; set; }
    public int? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? Subtotal { get; private set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public Quotation? Quotation { get; set; }
}

/// <summary>GCS object metadata attached to a quotation.</summary>
public sealed class QuotationFile
{
    public int Id { get; set; }
    public int QuotationId { get; set; }
    public string Bucket { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public Quotation? Quotation { get; set; }
}

/// <summary>Legacy quotation-to-order association.</summary>
public sealed class QuotationOrderLink
{
    public int Id { get; set; }
    public int QuotationId { get; set; }
    public int OrderId { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
    public Quotation? Quotation { get; set; }
}

/// <summary>Legacy inbound quotation request.</summary>
public sealed class QuotationRequest
{
    public int Id { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Email { get; set; }
    public string? TelephoneNumber { get; set; }
    public string? Country { get; set; }
    public string? CompanyName { get; set; }
    public string? TaxIdentification { get; set; }
    public string? Message { get; set; }
    public string? InternalComment { get; set; }
    public bool? Done { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>GCS object metadata attached to a quotation request.</summary>
public sealed class QuotationRequestFile
{
    public int Id { get; set; }
    public int? RequestId { get; set; }
    public string? Bucket { get; set; }
    public string? ObjectName { get; set; }
    public DateTime? CreatedDate { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>Hash-only durable binding for replay-safe quotation request creation.</summary>
public sealed class RequestCreateIdempotency
{
    public string KeyHash { get; set; } = string.Empty;
    public string Fingerprint { get; set; } = string.Empty;
    public int RequestId { get; set; }
}
