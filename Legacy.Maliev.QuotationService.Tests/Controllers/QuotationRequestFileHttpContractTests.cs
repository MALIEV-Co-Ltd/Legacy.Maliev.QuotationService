using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>
/// Isolated MVC routing/serialization contract, not an authentication or persistence test.
/// The actual controller runs with mocked boundaries and explicitly anonymous test endpoints.
/// </summary>
public sealed class QuotationRequestFileHttpContractTests
{
    [Fact]
    public async Task CreateRequestFile_OverHttp_ReturnsAbsoluteLocationAndPascalCaseBody()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        service.Setup(value => value.CreateRequestFileAsync(
                417, "legacy-requests", "instant-quotation/417/file.stl", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QuotationRequestFileResponse(
                23, 417, "legacy-requests", "instant-quotation/417/file.stl", null, null));
        var idempotency = new Mock<IIdempotencyStore>(MockBehavior.Strict);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(service.Object);
        builder.Services.AddSingleton(idempotency.Object);
        builder.Services.AddAuthorization(options => options.AddPolicy(
            "Permission:legacy.quotation-files.write:live_check",
            policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(QuotationRequestFilesController).Assembly)
            .AddJsonOptions(options =>
            {
                // Mirror Program.cs compatibility settings; this isolated host does not run Program.
                options.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
                options.JsonSerializerOptions.PropertyNamingPolicy = null;
                options.JsonSerializerOptions.DictionaryKeyPolicy = null;
            });
        await using var app = builder.Build();
        // Deliberate fixture-only bypass: live IAM/authentication is covered separately.
        app.MapControllers().AllowAnonymous();
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://quotation.example.test/");

        using var response = await client.PostAsync(
            "/quotationrequests/417/files?bucket=legacy-requests&objectName=instant-quotation%2F417%2Ffile.stl",
            content: null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var location = Assert.IsType<Uri>(response.Headers.Location);
        Assert.True(location.IsAbsoluteUri);
        Assert.Equal("https://quotation.example.test/quotationrequests/files/23", location.AbsoluteUri);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(new[] { "Id", "RequestId", "Bucket", "ObjectName" },
            body.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(23, body.GetProperty("Id").GetInt32());
        Assert.Equal(417, body.GetProperty("RequestId").GetInt32());
        Assert.Equal("legacy-requests", body.GetProperty("Bucket").GetString());
        Assert.Equal("instant-quotation/417/file.stl", body.GetProperty("ObjectName").GetString());
        service.VerifyAll();
        service.VerifyNoOtherCalls();
        // No Idempotency-Key was supplied, so this contract must not access Redis.
        idempotency.VerifyNoOtherCalls();
    }
}
