using System.Security.Claims;

namespace Legacy.Maliev.QuotationService.Api.Authorization;

/// <summary>Recognizes the authenticated employee actor emitted by the current Auth issuer.</summary>
public static class QuotationEmployeeActorPolicy
{
    /// <summary>Dedicated actor policy; route permissions remain independently required.</summary>
    public const string Name = "QuotationEmployeeActor";

    /// <summary>Checks actor shape only, after ordinary JWT authentication; it does not grant permissions or validate session lifecycle.</summary>
    public static bool IsEmployee(ClaimsPrincipal user) => QualificationEmployeeActor.Get(user) is not null;
}
