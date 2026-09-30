using System.Security.Claims;

namespace Legacy.Maliev.QuotationService.Api.Authorization;

internal static class QualificationEmployeeActor
{
    internal static string? Get(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;
        var subjects = user.FindAll("sub").ToArray();
        var kinds = user.FindAll("identity_kind").ToArray();
        if (subjects.Length != 1 || kinds.Length != 1
            || !string.Equals(kinds[0].Value, "employee", StringComparison.Ordinal)) return null;
        var subject = subjects[0].Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256
            || subject.StartsWith("service:", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var type in new[] { "user_id", ClaimTypes.NameIdentifier })
        {
            var aliases = user.FindAll(type).ToArray();
            if (aliases.Length > 1 || aliases.Any(alias => !string.Equals(alias.Value, subject, StringComparison.Ordinal)))
                return null;
        }
        return subject;
    }
}
