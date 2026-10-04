using System.Text.Json.Serialization;
using Legacy.Maliev.QuotationService.Api.Clients;
using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Services;
using Legacy.Maliev.QuotationService.Api.Workers;
using Legacy.Maliev.QuotationService.Api.Analytics;
using Legacy.Maliev.QuotationService.Data;
using Maliev.Aspire.ServiceDefaults;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
builder.AddDefaultApiVersioning();
builder.AddPostgresDbContext<QuotationDbContext>(connectionName: "QuotationDbContext");
builder.AddPostgresDbContext<QuotationRequestDbContext>(connectionName: "QuotationRequestDbContext");
builder.AddStandardCache("legacy:quotation:");
builder.AddStandardCors();
builder.AddJwtAuthentication();
builder.Services.AddAuthorization(options => options.AddPolicy(QuotationEmployeeActorPolicy.Name,
    policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
        QuotationEmployeeActorPolicy.IsEmployee(context.User))));
builder.AddLegacyAuthServiceTokenExchange();
builder.AddQuotationIamClient();
builder.Services.AddOptions<QualificationAuthorityOptions>().Bind(builder.Configuration.GetSection("QualificationAuthority"));
builder.Services.AddScoped<QualificationAuthorityClient>();
builder.Services.AddHttpClient(QualificationAuthorityClient.ClientName, client =>
{
    client.BaseAddress = QualificationAuthorityClient.ResolveOrigin(builder.Configuration["Services:Auth:BaseUrl"] ?? builder.Configuration["Services:Auth"]);
    client.Timeout = TimeSpan.FromSeconds(10);
}).ConfigurePrimaryHttpMessageHandler((handler, _) =>
{
    if (handler is SocketsHttpHandler sockets) sockets.AllowAutoRedirect = false;
    else if (handler is HttpClientHandler http) http.AllowAutoRedirect = false;
    else throw new InvalidOperationException("Qualification authority requires a redirect-disabled primary handler.");
}).AddServiceDiscovery().AddLegacyServiceAuthentication();
builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
builder.AddStandardOpenApi(title: "Legacy MALIEV Quotation Service API", description: "Temporary .NET 10 compatibility API for quotation and quotation-request contracts.");
// Register in this compilation so the generator attaches API and referenced DTO XML comments.
builder.Services.AddOpenApi("v1");
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    options.SerializerOptions.PropertyNamingPolicy = null;
    options.SerializerOptions.DictionaryKeyPolicy = null;
});
builder.Services.AddControllers().AddJsonOptions(options => { options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; options.JsonSerializerOptions.PropertyNamingPolicy = null; options.JsonSerializerOptions.DictionaryKeyPolicy = null; });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DistributedQuotationCache>();
builder.Services.AddScoped<IQuotationCache>(provider => provider.GetRequiredService<DistributedQuotationCache>());
builder.Services.AddScoped<IIdempotencyStore>(provider => provider.GetRequiredService<DistributedQuotationCache>());
builder.Services.AddScoped<IQuotationService, QuotationRepository>();
builder.Services.AddScoped<IQuotationDecisionWorkflow, QuotationDecisionWorkflow>();
builder.Services.AddHttpClient<IOrderDecisionClient, OrderDecisionClient>(client =>
{
    client.BaseAddress = new Uri(
        builder.Configuration["Services:Order:BaseUrl"]
        ?? builder.Configuration["Services:Order"]
        ?? "https+http://legacy-maliev-order-service");
    client.Timeout = TimeSpan.FromSeconds(30);
})
.AddServiceDiscovery()
.AddLegacyServiceAuthentication()
.AddStandardResilienceHandler();
builder.Services.AddHostedService<ExpiredQuotationWorker>();
builder.Services.AddOptions<GoogleAnalyticsMeasurementProtocolOptions>()
    .Bind(builder.Configuration.GetSection("GoogleAnalyticsMeasurementProtocol"))
    .Validate(options => !options.Enabled || !string.IsNullOrWhiteSpace(options.MeasurementId)
        && !string.IsNullOrWhiteSpace(options.ApiSecret), "Analytics credentials are required when delivery is enabled.")
    .Validate(options => options.BatchSize is > 0 and <= 100 && options.LeaseSeconds > 0
        && options.MaxAttempts > 0 && options.PollSeconds > 0, "Analytics delivery bounds are invalid.")
    .ValidateOnStart();
builder.Services.AddScoped<GoogleAnalyticsOutboxStore>();
builder.Services.AddScoped<IGoogleAnalyticsOutboxStore>(services => services.GetRequiredService<GoogleAnalyticsOutboxStore>());
builder.Services.AddHttpClient<GoogleAnalyticsDeliveryProcessor>(client => client.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
if (!string.Equals(builder.Configuration["MALIEV_OBSERVABILITY_STANDBY"], "true", StringComparison.Ordinal))
    builder.Services.AddHostedService<GoogleAnalyticsOutboxWorker>();

var app = builder.Build();
app.UseStandardMiddleware(); app.UseCors(); app.UseAuthentication(); app.UseAuthorization();
app.MapDefaultEndpoints("quotation"); app.MapControllers(); app.MapApiDocumentation(servicePrefix: "quotation");
await app.RunAsync();

public partial class Program;
