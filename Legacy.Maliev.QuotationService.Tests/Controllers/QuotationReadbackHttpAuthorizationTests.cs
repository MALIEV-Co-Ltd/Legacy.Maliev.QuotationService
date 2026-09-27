using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Legacy.Maliev.QuotationService.Api.Authorization;
using Legacy.Maliev.QuotationService.Api.Controllers;
using Legacy.Maliev.QuotationService.Application.Interfaces;
using Legacy.Maliev.QuotationService.Application.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

public sealed class QuotationReadbackHttpAuthorizationTests
{
    private static readonly DateTime FromUtc = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = FromUtc.AddDays(1);

    [Fact]
    public async Task QualificationReadback_HttpAuthorizationRejectsUntrustedCallersBeforeService()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        await using var app = await StartAppAsync(service.Object);
        using var client = app.GetTestClient();

        using var anonymous = await SendAsync(client, null);
        using var customer = await SendAsync(client, "customer-read");
        using var employeeWithoutPermission = await SendAsync(client, "employee-no-read");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, customer.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, employeeWithoutPermission.StatusCode);
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task QualificationReadback_AuthorizedEmployeeGetsPiiFreeNoStoreReceipt()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        service.Setup(value => value.GetQualificationOutcomeReadbackAsync(
                FromUtc, ToUtc, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QualificationOutcomeReadback(FromUtc, ToUtc,
                [new(7, FromUtc.AddHours(1), "request-7", null, "qualified")]));
        await using var app = await StartAppAsync(service.Object);
        using var client = app.GetTestClient();

        using var response = await SendAsync(client, "employee-read");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("request-7", document.RootElement.GetProperty("requests")[0].GetProperty("transactionId").GetString());
        Assert.DoesNotContain("email", document.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        service.VerifyAll();
        service.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task QualificationReceipt_DirectUrlRequiresReadPermissionBeforeLookup()
    {
        var service = new Mock<IQuotationService>(MockBehavior.Strict);
        service.Setup(value => value.GetRequestQualificationAsync(17, It.IsAny<CancellationToken>()))
            .ReturnsAsync((QualificationReceipt?)null);
        await using var app = await StartAppAsync(service.Object);
        using var client = app.GetTestClient();

        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/QuotationRequests/17/qualification-receipt");
        deniedRequest.Headers.Add("X-Fixture-Identity", "employee-no-read");
        using var denied = await client.SendAsync(deniedRequest);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        service.VerifyNoOtherCalls();

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/QuotationRequests/17/qualification-receipt");
        allowedRequest.Headers.Add("X-Fixture-Identity", "employee-read");
        using var missing = await client.SendAsync(allowedRequest);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        service.Verify(value => value.GetRequestQualificationAsync(17, It.IsAny<CancellationToken>()), Times.Once);
        service.VerifyNoOtherCalls();
    }

    private static async Task<WebApplication> StartAppAsync(IQuotationService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(service);
        builder.Services.AddAuthentication("Fixture")
            .AddScheme<AuthenticationSchemeOptions, FixtureAuthenticationHandler>("Fixture", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(
            $"Permission:{QuotationPermissions.RequestsRead}:live_check",
            policy => policy.RequireAuthenticatedUser().RequireClaim("permission", QuotationPermissions.RequestsRead)));
        builder.Services.AddControllers().AddApplicationPart(typeof(QuotationRequestsController).Assembly);
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string? identity)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"/QuotationRequests/qualification-outcomes/readback?fromUtc={Uri.EscapeDataString(FromUtc.ToString("O"))}&toUtc={Uri.EscapeDataString(ToUtc.ToString("O"))}");
        if (identity is not null) request.Headers.Add("X-Fixture-Identity", identity);
        return await client.SendAsync(request);
    }

    private sealed class FixtureAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var fixtureIdentity = Request.Headers["X-Fixture-Identity"].ToString();
            if (string.IsNullOrEmpty(fixtureIdentity))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, fixtureIdentity) };
            claims.Add(new Claim(ClaimTypes.Role, fixtureIdentity.StartsWith("employee-", StringComparison.Ordinal)
                ? "Employee" : "Customer"));
            if (fixtureIdentity is "employee-read" or "customer-read")
            {
                claims.Add(new Claim("permission", QuotationPermissions.RequestsRead));
            }

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
