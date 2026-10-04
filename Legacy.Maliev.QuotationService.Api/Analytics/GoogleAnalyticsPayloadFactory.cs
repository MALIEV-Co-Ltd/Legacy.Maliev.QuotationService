using System.Text.Json;
using Legacy.Maliev.QuotationService.Domain;

namespace Legacy.Maliev.QuotationService.Api.Analytics;

public static class GoogleAnalyticsPayloadFactory
{
    public static string Build(GoogleAnalyticsOutbox row)
    {
        var parameters = new Dictionary<string, object>
        {
            ["currency"] = row.Currency,
            ["value"] = row.Value,
            ["transaction_id"] = $"quotation-{row.QuotationId}",
            ["event_key"] = row.EventKey,
            ["session_id"] = row.SessionId,
            ["engagement_time_msec"] = 1,
        };
        if (row.SourceRequestId is { } requestId) parameters["source_transaction_id"] = $"request-{requestId}";
        if (row.SourceJourneyId is { } journeyId) parameters["journey_id"] = journeyId.ToString("D");
        var payload = new Dictionary<string, object>
        {
            ["client_id"] = row.ClientId,
            ["timestamp_micros"] = new DateTimeOffset(DateTime.SpecifyKind(row.OccurredUtc, DateTimeKind.Utc))
                .ToUnixTimeMilliseconds() * 1000,
            ["events"] = new[] { new Dictionary<string, object> { ["name"] = row.EventName, ["params"] = parameters } },
        };
        if (!string.IsNullOrWhiteSpace(row.UserId)) payload["user_id"] = row.UserId;
        return JsonSerializer.Serialize(payload);
    }
}
