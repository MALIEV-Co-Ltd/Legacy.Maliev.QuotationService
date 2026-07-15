using System.Text.Json.Serialization;
using Legacy.Maliev.QuotationService.Application.Interfaces;
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
builder.AddStandardMiddleware(options => options.EnableRequestLogging = true);
builder.AddStandardOpenApi(title: "Legacy MALIEV Quotation Service API", description: "Temporary .NET 10 compatibility API for quotation and quotation-request contracts.");
builder.Services.AddControllers().AddJsonOptions(options => { options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull; options.JsonSerializerOptions.PropertyNamingPolicy = null; options.JsonSerializerOptions.DictionaryKeyPolicy = null; });
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<DistributedQuotationCache>();
builder.Services.AddScoped<IQuotationCache>(provider => provider.GetRequiredService<DistributedQuotationCache>());
builder.Services.AddScoped<IIdempotencyStore>(provider => provider.GetRequiredService<DistributedQuotationCache>());
builder.Services.AddScoped<IQuotationService, QuotationRepository>();
builder.Services.AddHostedService<ExpiredQuotationWorker>();

var app = builder.Build();
app.UseStandardMiddleware(); app.UseCors(); app.UseAuthentication(); app.UseAuthorization();
app.MapDefaultEndpoints("quotation"); app.MapControllers(); app.MapApiDocumentation(servicePrefix: "quotation");
await app.RunAsync();

public partial class Program;
