using System.Buffers;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace Legacy.Maliev.QuotationService.Api.Serialization;

internal sealed class QuotationRequestOriginalBodyMvcOptionsSetup(ILoggerFactory loggers) : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions options)
    {
        if (options.InputFormatters.OfType<QuotationRequestOriginalBodyInputFormatter>().Any()) return;
        options.InputFormatters.Insert(0, new QuotationRequestOriginalBodyInputFormatter(options,
            loggers.CreateLogger<NewtonsoftJsonInputFormatter>()));
    }
}

// Input-only stock MVC composition; no global Newtonsoft registration or output change.
internal sealed class QuotationRequestOriginalBodyInputFormatter : IInputFormatter,
    IApiRequestFormatMetadataProvider, IInputFormatterExceptionPolicy
{
    private readonly NewtonsoftJsonInputFormatter inner;
    private readonly EmptyModelMetadataProvider metadata = new();

    internal QuotationRequestOriginalBodyInputFormatter(MvcOptions options, ILogger logger)
    {
        var source = new MvcNewtonsoftJsonOptions();
        source.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        source.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        inner = new NewtonsoftJsonInputFormatter(new PrivateFormatterLogger(logger),
            source.SerializerSettings, ArrayPool<char>.Shared, new DefaultObjectPoolProvider(), options, source);
    }

    public InputFormatterExceptionPolicy ExceptionPolicy => InputFormatterExceptionPolicy.MalformedInputExceptions;

    public bool CanRead(InputFormatterContext context) => SourceType(context.ModelType) is { } type
        && inner.CanRead(Context(context, type, new ModelStateDictionary()));

    public IReadOnlyList<string>? GetSupportedContentTypes(string? contentType, Type objectType) =>
        SourceType(objectType) is { } type ? inner.GetSupportedContentTypes(contentType, type) : null;

    public async Task<InputFormatterResult> ReadAsync(InputFormatterContext context)
    {
        var type = SourceType(context.ModelType)
            ?? throw new InvalidOperationException("Unsupported quotation request body type.");
        var state = new ModelStateDictionary { MaxAllowedErrors = context.ModelState.MaxAllowedErrors };
        InputFormatterResult result;
        try
        {
            // Stock MVC owns encoding, async draining, buffering, parsing and reader disposal.
            result = await inner.ReadAsync(Context(context, type, state));
        }
        finally
        {
            foreach (var entry in state)
            {
                var key = SafePath(entry.Key, context.ModelName, type);
                foreach (var error in entry.Value.Errors)
                {
                    if (error.Exception is UnsupportedContentTypeException)
                        context.ModelState.TryAddModelError(key,
                            new UnsupportedContentTypeException("Unsupported request content type."), context.Metadata);
                    else
                        context.ModelState.TryAddModelError(key, "Invalid request body.");
                }
            }
        }

        if (result.HasError || !result.IsModelSet) return result;
        return InputFormatterResult.Success(result.Model switch
        {
            OriginalRequest value => new UpsertQuotationRequestRequest(value.FirstName, value.LastName,
                value.Email, value.TelephoneNumber, value.Country, value.CompanyName, value.TaxIdentification,
                value.Message, value.InternalComment, value.Done, value.JourneyId),
            OriginalRequestFile value => new UpsertQuotationRequestFileRequest(
                value.RequestId, value.Bucket, value.ObjectName),
            null => null,
            _ => throw new InvalidOperationException("Unexpected quotation request body model.")
        });
    }

    private InputFormatterContext Context(InputFormatterContext outer, Type type, ModelStateDictionary state) =>
        new(outer.HttpContext, outer.ModelName, state, metadata.GetMetadataForType(type),
            outer.ReaderFactory, outer.TreatEmptyInputAsDefaultValue);

    private static Type? SourceType(Type type) => type == typeof(UpsertQuotationRequestRequest)
        ? typeof(OriginalRequest) : type == typeof(UpsertQuotationRequestFileRequest)
            ? typeof(OriginalRequestFile) : null;

    private static string SafePath(string key, string prefix, Type type)
    {
        if (key.Length > 512 || prefix is not ("" or "item" or "request")) return "$";
        var path = key;
        if (prefix.Length > 0)
        {
            if (key == prefix) return prefix;
            if (!key.StartsWith(prefix + ".", StringComparison.Ordinal)) return "$";
            path = key[(prefix.Length + 1)..];
        }
        if (path.Length == 0) return key;
        if (path is "$id" or "$ref" or "$type" or "$values") return key;
        return type.GetProperties().Any(property => property.Name.Equals(path, StringComparison.OrdinalIgnoreCase))
            ? key : "$";
    }

    private sealed class PrivateFormatterLogger(ILogger target) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            target.BeginScope("Quotation request input formatter");
        public bool IsEnabled(LogLevel level) => target.IsEnabled(level);
        public void Log<TState>(LogLevel level, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            target.Log(level, eventId, "Quotation request input diagnostic; exception type {ExceptionType}",
                exception?.GetType().FullName ?? "None");
    }

#nullable disable
    // Complete final source shapes, not EF entities. Discarded server-owned fields
    // still undergo original parsing; only existing public DTO slots are projected.
    private sealed class OriginalRequest
    {
        public int Id { get; set; }
        public Guid? JourneyId { get; set; }
        public string TransactionId { get; set; }
        public string QualificationState { get; set; }
        public DateTime? QualificationStateChangedUtc { get; set; }
        public int QualificationVersion { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string TelephoneNumber { get; set; }
        public string Country { get; set; }
        public string CompanyName { get; set; }
        public string TaxIdentification { get; set; }
        public string Message { get; set; }
        public string InternalComment { get; set; }
        public bool? Done { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
    }

    private sealed class OriginalRequestFile
    {
        public int Id { get; set; }
        public int? RequestId { get; set; }
        public string Bucket { get; set; }
        public string ObjectName { get; set; }
        public DateTime? CreatedDate { get; set; }
        public DateTime? ModifiedDate { get; set; }
    }
#nullable restore
}
