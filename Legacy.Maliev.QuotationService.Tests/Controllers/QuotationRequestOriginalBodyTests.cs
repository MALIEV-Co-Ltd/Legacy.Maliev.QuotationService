using System.Buffers;
using System.Text;
using Legacy.Maliev.QuotationService.Api.Serialization;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.ObjectPool;
using Newtonsoft.Json;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Inert source-shaped stock-reader oracle; no host, database or network.</summary>
public sealed class QuotationRequestOriginalBodyTests
{
    [Theory]
    [InlineData(false, "{FirstName:42,LastName:false,Done:'true',Done:null}", "success", "[\"42\",\"false\",true,null]")]
    [InlineData(false, "/* source */ {'FirstName':'literal','FirstName':null,'Done':'false',}", "success", "[\"literal\",null,false,null]")]
    [InlineData(false, "{'FirstName':'literal'} trailing ignored", "success", "[\"literal\",null,null,null]")]
    [InlineData(false, "{'Id':'17','CreatedDate':'2000-01-01T00:00:00Z','QualificationVersion':'3'}", "success", "[null,null,null,null]")]
    [InlineData(false, "{'Id':{'private-input-marker':1}}", "failure", null)]
    [InlineData(false, "{'QualificationVersion':{'private-input-marker':1}}", "failure", null)]
    [InlineData(false, "{'Done':{'private-input-marker':1}}", "failure", null)]
    [InlineData(false, "null", "no-value", null)]
    [InlineData(false, "  ", "no-value", null)]
    [InlineData(true, "{RequestId:'17',RequestId:null,Bucket:42,ObjectName:false}", "success", "[17,\"42\",\"false\"]")]
    [InlineData(true, "{RequestId:'',Bucket:'B',ObjectName:'O'}", "success", "[null,\"B\",\"O\"]")]
    [InlineData(true, "{ModifiedDate:{'private-input-marker':1}}", "failure", null)]
    [InlineData(false, "{CreatedDate:{'private-input-marker':1}}", "failure", null)]
    [InlineData(false, "{ModifiedDate:{'private-input-marker':1}}", "failure", null)]
    [InlineData(false, "{QualificationStateChangedUtc:{'private-input-marker':1}}", "failure", null)]
    [InlineData(true, "{Id:{'private-input-marker':1}}", "failure", null)]
    [InlineData(true, "{CreatedDate:{'private-input-marker':1}}", "failure", null)]
    [InlineData(true, "{RequestId:{'private-input-marker':1}}", "failure", null)]
    [InlineData(true, "[]", "failure", null)]
    [InlineData(true, "null", "no-value", null)]
    public async Task StockOriginalShapeAndRegisteredCandidate_PreserveLiteralOutcomes(
        bool file, string json, string expected, string? projection)
    {
        var mvc = new MvcOptions();
        new QuotationRequestOriginalBodyMvcOptionsSetup(NullLoggerFactory.Instance).Configure(mvc);
        var candidate = Assert.Single(mvc.InputFormatters);
        var settings = new MvcNewtonsoftJsonOptions();
        settings.SerializerSettings.NullValueHandling = NullValueHandling.Ignore;
        settings.SerializerSettings.ReferenceLoopHandling = ReferenceLoopHandling.Ignore;
        var oracle = new NewtonsoftJsonInputFormatter(NullLogger.Instance, settings.SerializerSettings,
            ArrayPool<char>.Shared, new DefaultObjectPoolProvider(), new MvcOptions(), settings);
        var source = await ReadAsync(oracle, file ? typeof(OriginalFile) : typeof(OriginalRequest), json);
        Assert.Equal(expected, source.Kind);
        if (expected == "success") Assert.Equal(projection, Project(source.Result.Model));
        var actual = await ReadAsync(candidate,
            file ? typeof(UpsertQuotationRequestFileRequest) : typeof(UpsertQuotationRequestRequest), json);
        Assert.Equal(expected, actual.Kind);
        if (expected == "success") Assert.Equal(projection, Project(actual.Result.Model));
        if (expected == "failure")
        {
            Assert.True(actual.State.ErrorCount > 0);
            Assert.All(actual.State.Values.SelectMany(value => value.Errors), error =>
            {
                Assert.DoesNotContain("private-input-marker", error.ErrorMessage, StringComparison.Ordinal);
                Assert.Null(error.Exception);
            });
            Assert.DoesNotContain(actual.State.Keys, key => key.Contains("private-input-marker", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SelectedContractsOnly_LeaveModernBodiesAndOutputsUntouched()
    {
        var options = new MvcOptions();
        new QuotationRequestOriginalBodyMvcOptionsSetup(NullLoggerFactory.Instance).Configure(options);
        var formatter = Assert.Single(options.InputFormatters);
        foreach (var type in new[] { typeof(UpsertQuotationRequest), typeof(UpsertQuotationFileRequest),
            typeof(QualificationStateUpdateRequest) })
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{}"));
            var http = new DefaultHttpContext();
            http.Request.Body = stream;
            http.Request.ContentType = "application/json";
            var context = new InputFormatterContext(http, "", new ModelStateDictionary(),
                new EmptyModelMetadataProvider().GetMetadataForType(type),
                (body, encoding) => new HttpRequestStreamReader(body, encoding));
            Assert.False(formatter.CanRead(context));
        }
        Assert.Empty(options.OutputFormatters);
    }

    private static string Project(object? model) => System.Text.Json.JsonSerializer.Serialize(model switch
    {
        OriginalRequest value => new object?[] { value.FirstName, value.LastName, value.Done, value.JourneyId },
        UpsertQuotationRequestRequest value => new object?[] { value.FirstName, value.LastName, value.Done, value.JourneyId },
        OriginalFile value => new object?[] { value.RequestId, value.Bucket, value.ObjectName },
        UpsertQuotationRequestFileRequest value => new object?[] { value.RequestId, value.Bucket, value.ObjectName },
        _ => throw new InvalidOperationException("Unexpected oracle projection.")
    });

    private static async Task<(string Kind, InputFormatterResult Result, ModelStateDictionary State)> ReadAsync(
        IInputFormatter formatter, Type type, string json)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var http = new DefaultHttpContext();
        http.Request.Body = stream;
        http.Request.ContentType = "application/json";
        var state = new ModelStateDictionary();
        var context = new InputFormatterContext(http, "", state,
            new EmptyModelMetadataProvider().GetMetadataForType(type),
            (body, encoding) => new HttpRequestStreamReader(body, encoding));
        Assert.True(formatter.CanRead(context));
        var result = await formatter.ReadAsync(context);
        Assert.True(stream.CanRead);
        return (result.HasError ? "failure" : result.IsModelSet ? "success" : "no-value", result, state);
    }

#nullable disable
    // Independent inert copies of the complete committed final source shapes.
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

    private sealed class OriginalFile
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
