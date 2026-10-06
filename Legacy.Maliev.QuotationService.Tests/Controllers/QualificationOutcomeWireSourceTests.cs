using System.Text;
using System.Text.Json.Serialization;
using QualificationOutcomeWireSource;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Exact UTF8 bytes from actual DTO/action/MVC serializer; no routing, issuer or database claim.</summary>
public sealed class QualificationOutcomeWireSourceTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("mixed")]
    public async Task ActualControllerSerializer_EmitsReviewedSyntheticWire(string caseName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var emission = await QualificationOutcomeWire.RenderAsync(caseName, timeout.Token);
        var expected = caseName == "empty"
            ? """{"fromUtc":"2026-01-01T00:00:00Z","toUtc":"2026-01-02T00:00:00Z","requests":[]}"""
            : """{"fromUtc":"2026-01-01T00:00:00Z","toUtc":"2026-01-02T00:00:00Z","requests":[{"requestId":101,"createdUtc":"2026-01-01T01:00:00Z","transactionId":"synthetic-request-101","journeyId":"11111111-1111-4111-8111-111111111111","state":"qualified"},{"requestId":102,"createdUtc":"2026-01-01T02:00:00Z","state":"unreviewed"},{"requestId":103,"createdUtc":"2026-01-01T03:00:00Z","transactionId":"synthetic-request-103","state":"duplicate"}]}""";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), emission.Bytes);
        Assert.Equal(200, emission.StatusCode);
        Assert.Equal("application/json; charset=utf-8", emission.ContentType);
        Assert.True(emission.CamelCase);
        Assert.Equal(JsonIgnoreCondition.WhenWritingNull, emission.IgnoreCondition);
        Assert.Equal("Legacy.Maliev.QuotationService.Application.Models.QualificationOutcomeReadback", emission.ActualDtoType);
        Assert.Equal("Microsoft.AspNetCore.Mvc.Infrastructure.SystemTextJsonResultExecutor", emission.ActualMvcExecutorType);
    }
}
