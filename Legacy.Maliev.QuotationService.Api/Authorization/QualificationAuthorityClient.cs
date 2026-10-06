using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maliev.Aspire.ServiceDefaults.Diagnostics;

namespace Legacy.Maliev.QuotationService.Api.Authorization;

public sealed class QualificationAuthorityClient(IHttpClientFactory factory, ILogger<QualificationAuthorityClient> logger)
{
    internal const string ClientName = "QualificationAuthority";
    private const string Purpose = QualificationAuthorityAttribute.Purpose;

    internal async Task<int> CheckAsync(string employeeAccessToken, string subject, string permission, int requestId, CancellationToken cancellationToken)
    {
        if (employeeAccessToken.Length > 16384 || requestId <= 0 || permission is not (QuotationPermissions.RequestsRead or QuotationPermissions.RequestsUpdate)) return 403;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            return await CheckWithinDeadlineAsync(employeeAccessToken, subject, permission, requestId, cancellationToken, deadline.Token);
        }
        catch (InvalidOperationException) { return 503; }
    }

    // Same production operation body; internal entry point lets regression supply a genuine real
    // linked ten-second token whose cancellation can be awaited by the controlled SDK sink.
    internal async Task<int> CheckWithinDeadlineAsync(string employeeAccessToken, string subject, string permission,
        int requestId, CancellationToken cancellationToken, CancellationToken operationToken)
    {
        var observation = new PrivateDependencyFailureObservation();
        var phase = ObservationPhase.Headers;
        int? responseStatus = null;
        try
        {
            using var client = factory.CreateClient(ClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/auth/v1/introspection/quotation-qualification")
            { Content = JsonContent.Create(new { employeeAccessToken, permission, purpose = Purpose, requestId }) };
            using var response = await client.SendWithPrivateFailureObservationAsync(request, operationToken, observation);
            responseStatus = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return 403;
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > 4096) return 503;
            phase = ObservationPhase.Body;
            await using var stream = await response.Content.ReadAsStreamAsync(operationToken);
            using var bounded = new MemoryStream();
            var buffer = new byte[4097];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, 4097 - (int)bounded.Length), operationToken);
                if (read == 0) break;
                bounded.Write(buffer, 0, read);
                if (bounded.Length > 4096) return 503;
            }
            using var json = JsonDocument.Parse(bounded.ToArray());
            var body = json.RootElement;
            if (body.ValueKind != JsonValueKind.Object) return 503;
            var fields = body.EnumerateObject().ToArray();
            if (fields.Length != 5 || fields.Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 5 ||
                fields.Any(x => x.Name is not ("allowed" or "subject" or "permission" or "purpose" or "requestId"))) return 503;
            if (body.GetProperty("permission").ValueKind != JsonValueKind.String || body.GetProperty("permission").GetString() != permission ||
                body.GetProperty("purpose").ValueKind != JsonValueKind.String || body.GetProperty("purpose").GetString() != Purpose ||
                body.GetProperty("requestId").ValueKind != JsonValueKind.Number || !body.GetProperty("requestId").TryGetInt32(out var id) || id != requestId) return 503;
            var allowed = body.GetProperty("allowed");
            var actor = body.GetProperty("subject");
            if (allowed.ValueKind == JsonValueKind.False) return actor.ValueKind == JsonValueKind.Null ? 403 : 503;
            return allowed.ValueKind == JsonValueKind.True && actor.ValueKind == JsonValueKind.String && actor.GetString() == subject ? 200 : 503;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or InvalidOperationException ||
            exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var outcome = ClassifyLateFailure(exception, phase, operationToken);
            if (outcome is { } observed && !observation.WasObserved) RecordLateFailure(observed, responseStatus);
            return 503;
        }
    }

    // The selected SDK owns terminal header HTTP/transport failures and native HttpClient timeout.
    // The consumer owns its linked deadline and body IO after headers, without guessing native provenance.
    private enum ObservationPhase
    {
        Headers,
        Body
    }
    private enum LateFailureOutcome
    {
        QualificationHeadersDeadline,
        QualificationBodyDeadline,
        QualificationHeadersTransport,
        QualificationBodyTransport
    }

    private static LateFailureOutcome? ClassifyLateFailure(Exception exception, ObservationPhase phase, CancellationToken operationToken)
    {
        if (exception is OperationCanceledException && operationToken.IsCancellationRequested)
            return phase == ObservationPhase.Body ? LateFailureOutcome.QualificationBodyDeadline : LateFailureOutcome.QualificationHeadersDeadline;
        if (phase == ObservationPhase.Body && exception is HttpRequestException or IOException or OperationCanceledException)
            return LateFailureOutcome.QualificationBodyTransport;
        if (phase == ObservationPhase.Headers && exception is IOException)
            return LateFailureOutcome.QualificationHeadersTransport;
        return null;
    }

    private void RecordLateFailure(LateFailureOutcome outcome, int? status)
    {
        try
        {
            // Code-owned enum only. No exception object, URI, tokens, subject, permission or request ID.
            if (status is { } knownStatus)
                logger.LogError(new EventId(5101, "DependencyRequestFailure"),
                    "{EventName} Dependency={Dependency} Operation={Operation} StatusCode={StatusCode}",
                    "DependencyRequestFailure", ClientName, outcome.ToString(), knownStatus);
            else
                logger.LogError(new EventId(5101, "DependencyRequestFailure"),
                    "{EventName} Dependency={Dependency} Operation={Operation}",
                    "DependencyRequestFailure", ClientName, outcome.ToString());
        }
        catch (Exception)
        {
            // Observation failure cannot replace an authority outcome or disclose its own details.
        }
    }

    internal static Uri ResolveOrigin(string? configured)
    {
        if (configured is null) return new Uri("https+http://legacy-maliev-auth-service");
        if (string.IsNullOrWhiteSpace(configured) || configured != configured.Trim() ||
            !Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http" or "https+http") || string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Qualification authority origin is invalid.");
        return uri;
    }
}
