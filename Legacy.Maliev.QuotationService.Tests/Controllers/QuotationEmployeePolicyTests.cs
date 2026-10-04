using System.Security.Claims;
using Legacy.Maliev.QuotationService.Api.Authorization;

namespace Legacy.Maliev.QuotationService.Tests.Controllers;

/// <summary>Actor-shape regression only; actual issuer/JWT/IAM HTTP evidence remains a separate proof.</summary>
public sealed class QuotationEmployeePolicyTests
{
    private static ClaimsPrincipal Principal(IEnumerable<Claim> claims, bool authenticated = true)
        => new(new ClaimsIdentity(claims, authenticated ? "actor-shape-test" : null));

    [Fact]
    public void CurrentIssuerEmployee_DoesNotRequireSyntheticRole()
    {
        var user = Principal([new("sub", "employee-42"), new("identity_kind", "employee"),
            new("sid", "e5ba4f54-ea90-4668-a60c-71c9b3e8792c")]);
        Assert.False(user.IsInRole("Employee"));
        Assert.True(QuotationEmployeeActorPolicy.IsEmployee(user));
        Assert.Empty(user.FindAll("permissions")); // Actor recognition itself is not a permission grant.
    }

    [Fact]
    public void StableMatchingAliases_PreserveCanonicalEmployee()
        => Assert.True(QuotationEmployeeActorPolicy.IsEmployee(Principal([
            new("sub", "employee-42"), new("identity_kind", "employee"),
            new("user_id", "employee-42"), new(ClaimTypes.NameIdentifier, "employee-42")])));

    public static TheoryData<string> RejectedShapes => new()
    {
        "anonymous", "role-only", "customer-with-role", "service-with-role", "service-prefix",
        "mixed-case-service-prefix", "missing-sub", "blank-sub", "long-sub", "duplicate-sub",
        "missing-kind", "duplicate-kind", "wrong-case-kind", "conflicting-user-id", "duplicate-user-id",
        "conflicting-name-identifier", "duplicate-name-identifier",
    };

    [Theory, MemberData(nameof(RejectedShapes))]
    public void InvalidActorShape_IsDeniedDespiteEmployeeRole(string profile)
    {
        var claims = new List<Claim> { new("sub", "employee-42"), new("identity_kind", "employee"), new(ClaimTypes.Role, "Employee") };
        switch (profile)
        {
            case "role-only": case "missing-kind": claims.RemoveAll(x => x.Type == "identity_kind"); break;
            case "customer-with-role": claims.RemoveAll(x => x.Type == "identity_kind"); claims.Add(new("identity_kind", "customer")); break;
            case "service-with-role": claims.RemoveAll(x => x.Type == "identity_kind"); claims.Add(new("identity_kind", "service")); break;
            case "service-prefix": claims.RemoveAll(x => x.Type == "sub"); claims.Add(new("sub", "service:legacy-intranet")); break;
            case "mixed-case-service-prefix": claims.RemoveAll(x => x.Type == "sub"); claims.Add(new("sub", "SERVICE:legacy-intranet")); break;
            case "missing-sub": claims.RemoveAll(x => x.Type == "sub"); break;
            case "blank-sub": claims.RemoveAll(x => x.Type == "sub"); claims.Add(new("sub", " ")); break;
            case "long-sub": claims.RemoveAll(x => x.Type == "sub"); claims.Add(new("sub", new string('e', 257))); break;
            case "duplicate-sub": claims.Add(new("sub", "employee-42")); break;
            case "duplicate-kind": claims.Add(new("identity_kind", "employee")); break;
            case "wrong-case-kind": claims.RemoveAll(x => x.Type == "identity_kind"); claims.Add(new("identity_kind", "Employee")); break;
            case "conflicting-user-id": claims.Add(new("user_id", "employee-other")); break;
            case "duplicate-user-id": claims.Add(new("user_id", "employee-42")); claims.Add(new("user_id", "employee-42")); break;
            case "conflicting-name-identifier": claims.Add(new(ClaimTypes.NameIdentifier, "employee-other")); break;
            case "duplicate-name-identifier": claims.Add(new(ClaimTypes.NameIdentifier, "employee-42")); claims.Add(new(ClaimTypes.NameIdentifier, "employee-42")); break;
        }
        Assert.False(QuotationEmployeeActorPolicy.IsEmployee(Principal(claims, profile != "anonymous")));
    }
}
