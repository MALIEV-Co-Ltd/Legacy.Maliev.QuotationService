using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace QualificationOutcomeWireSource;

/// <summary>Executes the actual readback action and its MVC JsonResult executor with synthetic DTO instances.</summary>
public static class QualificationOutcomeWire
{
    public static async Task<WireEmission> RenderAsync(string caseName, CancellationToken cancellationToken)
    {
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);
        IReadOnlyList<QualificationOutcomeReadbackRequest> requests = caseName switch
        {
            "empty" => [],
            "mixed" =>
            [
                new(101, from.AddHours(1), "synthetic-request-101",
                    Guid.Parse("11111111-1111-4111-8111-111111111111"), "qualified"),
                new(102, from.AddHours(2), null, null, "unreviewed"),
                new(103, from.AddHours(3), "synthetic-request-103", null, "duplicate"),
            ],
            _ => throw new InvalidDataException("Unknown reviewed synthetic wire case."),
        };
        var service = DispatchProxy.Create<IQuotationService, SyntheticQualificationReadback>();
        var fixture = (SyntheticQualificationReadback)(object)service;
        fixture.Receipt = new QualificationOutcomeReadback(from, to, requests);
        await using var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        await using var body = new MemoryStream();
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            RequestAborted = cancellationToken,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "synthetic-wire-employee"), new Claim("identity_kind", "employee")],
                "synthetic-wire-only")),
        };
        context.Response.Body = body;
        var controller = new QuotationRequestsController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };
        var action = await controller.GetQualificationOutcomeReadbackAsync(from, to, cancellationToken);
        if (action is not JsonResult result || result.Value is not QualificationOutcomeReadback
            || result.SerializerSettings is not JsonSerializerOptions options || fixture.Invocations != 1)
            throw new InvalidDataException("Actual controller did not return the expected DTO and serializer.");
        var executor = services.GetRequiredService<IActionResultExecutor<JsonResult>>();
        await result.ExecuteResultAsync(new ActionContext(context, new RouteData(), new ActionDescriptor()));
        await context.Response.BodyWriter.FlushAsync(cancellationToken);
        return new WireEmission(body.ToArray(), context.Response.StatusCode,
            context.Response.ContentType ?? "", options.PropertyNamingPolicy == JsonNamingPolicy.CamelCase,
            options.DefaultIgnoreCondition, result.Value.GetType().FullName ?? "",
            executor.GetType().FullName ?? "");
    }
}

/// <summary>Synthetic service boundary only. No DTO, controller or serializer implementation is copied.</summary>
public class SyntheticQualificationReadback : DispatchProxy
{
    public QualificationOutcomeReadback? Receipt { get; set; }
    public int Invocations { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IQuotationService.GetQualificationOutcomeReadbackAsync)
            || Receipt is null || args is not [DateTime from, DateTime to, CancellationToken token]
            || from != Receipt.FromUtc || to != Receipt.ToUtc)
            throw new InvalidDataException("Unexpected synthetic service call.");
        token.ThrowIfCancellationRequested();
        Invocations++;
        return Task.FromResult(Receipt);
    }
}

public sealed record WireEmission(byte[] Bytes, int StatusCode, string ContentType, bool CamelCase,
    JsonIgnoreCondition IgnoreCondition, string ActualDtoType, string ActualMvcExecutorType);
