namespace Legacy.Maliev.QuotationService.Api.Analytics;

public sealed class GoogleAnalyticsMeasurementProtocolOptions
{
    public bool Enabled { get; set; }
    public string MeasurementId { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 10;
    public int PollSeconds { get; set; } = 10;
}
