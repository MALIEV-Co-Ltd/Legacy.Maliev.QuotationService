using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
        if (Environment.GetEnvironmentVariable("QUOTATION_CANDIDATE_WIRE") == "1")
        {
            Assert.True(OperatingSystem.IsLinux());
            var repository = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
                ?? throw new InvalidDataException("Hosted workspace missing."), "candidate"));
            Assert.Equal(Path.Combine(repository, "Legacy.Maliev.QuotationService.Tests", "bin", "Release", "net10.0",
                "Legacy.Maliev.QuotationService.Tests.dll"), typeof(QualificationOutcomeWireSourceTests).Assembly.Location);
            Assert.Equal(typeof(QualificationOutcomeWireSourceTests).Assembly, typeof(QualificationOutcomeWire).Assembly);
            var output = Path.Combine(repository, "TestResults", "QualificationWire");
            Directory.CreateDirectory(output);
            await WriteNewAsync(Path.Combine(output, caseName + ".json"), emission.Bytes, timeout.Token);
            var assemblies = new[]
            {
                typeof(Legacy.Maliev.QuotationService.Application.Models.QualificationOutcomeReadback).Assembly,
                typeof(Legacy.Maliev.QuotationService.Api.Controllers.QuotationRequestsController).Assembly,
                typeof(QualificationOutcomeWire).Assembly,
            }.Select(assembly => new
            {
                name = assembly.GetName().Name,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location))),
            }).ToArray();
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new
            {
                caseName,
                emission.StatusCode,
                emission.ContentType,
                emission.CamelCase,
                ignoreCondition = emission.IgnoreCondition.ToString(),
                emission.ActualDtoType,
                emission.ActualMvcExecutorType,
                actualHarnessType = typeof(QualificationOutcomeWire).FullName,
                actualHarnessAssembly = typeof(QualificationOutcomeWire).Assembly.GetName().Name,
                assemblies,
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await WriteNewAsync(Path.Combine(output, caseName + ".metadata.json"), metadata, timeout.Token);
        }
    }

    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await stream.WriteAsync(bytes, token);
    }
}
