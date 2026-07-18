using System.Text.Json.Serialization;
using Legacy.Maliev.QuotationService.Api.Clients;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Services;
using Legacy.Maliev.QuotationService.Api.Workers;
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
builder.AddLegacyAuthServiceTokenExchange();
builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
builder.AddStandardOpenApi(title: "Legacy MALIEV Quotation Service API", description: "Temporary .NET 10 compatibility API for quotation and quotation-request contracts.");
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

var app = builder.Build();
app.UseStandardMiddleware(); app.UseCors(); app.UseAuthentication(); app.UseAuthorization();
app.MapDefaultEndpoints("quotation"); app.MapControllers(); app.MapApiDocumentation(servicePrefix: "quotation");
await app.RunAsync();

public partial class Program;
