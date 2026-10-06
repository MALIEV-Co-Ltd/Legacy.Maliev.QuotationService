using Maliev.Aspire.ServiceDefaults.IAM;

namespace Legacy.Maliev.QuotationService.Api.Authorization;

/// <summary>Normal-host live IAM transport, authenticated through the existing legacy workload exchange.</summary>
internal static class QuotationIamComposition
{
    internal static void AddQuotationIamClient(this IHostApplicationBuilder builder)
    {
        builder.Services.AddScoped<IIamServiceClient, IamServiceClient>();
        builder.Services.AddHttpClient("IAMService", client =>
        {
            client.BaseAddress = ResolveOrigin(
                builder.Configuration["Services:IAMService:BaseUrl"]
                ?? builder.Configuration["Services:IAM:BaseUrl"]
                ?? builder.Configuration["Services:IAM"], builder.Environment);
            client.Timeout = TimeSpan.FromSeconds(10);
        })
        .ConfigurePrimaryHttpMessageHandler((handler, _) =>
        {
            if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
            else if (handler is HttpClientHandler http) http.AllowAutoRedirect = false;
            else throw new InvalidOperationException("Live IAM requires a redirect-disabled primary handler.");
        })
        .RedactLoggedHeaders(["X-Maliev-IAM-Live-Check-Key"])
        .AddServiceDiscovery()
        .AddLegacyServiceAuthentication()
        .AddPrivateFailureObservation("IAMService");
    }

    private static Uri ResolveOrigin(string? configured, IHostEnvironment environment)
    {
        // Existing Defaults logical routing convention; not an authorization or credential fallback.
        if (configured is null) return new Uri("https+http://IAMService");
        if (string.IsNullOrWhiteSpace(configured) || configured != configured.Trim()
            || !Uri.TryCreate(configured, UriKind.Absolute, out var uri)
            || string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw InvalidOrigin();
        if (uri.Scheme == "https") return uri;
        if (uri.Scheme == "https+http" && uri.Port == -1
            && uri.Host is "iamservice" or "legacy-maliev-iam-service") return uri;
        if (uri.Scheme == "http" && (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            && uri.IsLoopback) return uri;
        throw InvalidOrigin();
    }

    private static InvalidOperationException InvalidOrigin() => new("Live IAM requires an approved service origin.");
}
