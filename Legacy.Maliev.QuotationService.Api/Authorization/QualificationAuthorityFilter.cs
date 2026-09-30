using System.Globalization;
using System.Net.Http.Headers;
using Maliev.Aspire.ServiceDefaults.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Legacy.Maliev.QuotationService.Api.Authorization;

[AttributeUsage(AttributeTargets.Method)]
public sealed class QualificationAuthorityAttribute(string permission) : Attribute, IFilterFactory
{
    public const string Purpose = "quotation-request-qualification";
    public string Permission { get; } = permission;
    public bool IsReusable => false;
    public IFilterMetadata CreateInstance(IServiceProvider services) => new QualificationAuthorityFilter(Permission,
        services.GetRequiredService<IAuthorizationService>(), services.GetRequiredService<IOptionsMonitor<QualificationAuthorityOptions>>(),
        () => services.GetRequiredService<QualificationAuthorityClient>());
}

internal sealed class QualificationAuthorityFilter(string permission, IAuthorizationService authorization,
    IOptionsMonitor<QualificationAuthorityOptions> options, Func<QualificationAuthorityClient> resolveClient) : IAsyncAuthorizationFilter
{
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated != true) { context.Result = new UnauthorizedResult(); return; }
        bool enabled;
        try { enabled = options.CurrentValue.Enabled; }
        catch (Exception exception) when (exception is OptionsValidationException or InvalidOperationException)
        { context.Result = new StatusCodeResult(503); return; }
        if (!enabled)
        {
            var policy = new RequirePermissionAttribute(permission) { RequireLiveCheck = true }.Policy!;
            if (!(await authorization.AuthorizeAsync(http.User, http, policy)).Succeeded) context.Result = new ForbidResult();
            return;
        }
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.Pragma = "no-cache";
        var subject = QualificationEmployeeActor.Get(http.User);
        if (subject is null) { context.Result = new ForbidResult(); return; }
        if (!int.TryParse(Convert.ToString(context.RouteData.Values["requestId"], CultureInfo.InvariantCulture),
            NumberStyles.None, CultureInfo.InvariantCulture, out var requestId) || requestId <= 0)
        { context.Result = new BadRequestResult(); return; }
        if (http.Request.Headers.Authorization.Count != 1 ||
            !AuthenticationHeaderValue.TryParse(http.Request.Headers.Authorization.ToString(), out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(header.Parameter) || header.Parameter.Length > 16384)
        { context.Result = new ForbidResult(); return; }
        QualificationAuthorityClient client;
        try { client = resolveClient(); }
        catch (InvalidOperationException) { context.Result = new StatusCodeResult(503); return; }
        var decision = await client.CheckAsync(header.Parameter, subject, permission, requestId, http.RequestAborted);
        if (decision != 200) context.Result = new StatusCodeResult(decision);
    }
}
