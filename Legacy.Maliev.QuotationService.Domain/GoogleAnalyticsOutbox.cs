namespace Legacy.Maliev.QuotationService.Domain;

/// <summary>Immutable consented first-acceptance payload with mutable delivery lease state.</summary>
public sealed class GoogleAnalyticsOutbox
{
    public long Id { get; set; }
    public int QuotationId { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public string EventName { get; set; } = string.Empty;
    public int? SourceRequestId { get; set; }
    public Guid? SourceJourneyId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string? UserId { get; set; }
    public string Currency { get; set; } = string.Empty;
    public decimal Value { get; set; }
    public DateTime OccurredUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public int AttemptCount { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntilUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public DateTime? FailedUtc { get; set; }
    public string? LastError { get; set; }
}
