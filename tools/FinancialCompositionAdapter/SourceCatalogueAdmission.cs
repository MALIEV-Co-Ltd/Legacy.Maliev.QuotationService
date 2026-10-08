extern alias IamApplication;

using IamApplication::Maliev.IAMService.Application.DTOs.Requests;
using IamApplication::Maliev.IAMService.Application.Validators;

namespace FinancialCompositionAdapter;

public sealed record SourcePermission(string PermissionId, string Description, string OwnerCommit, string SourcePath, int SourceLine);

public static class SourceCatalogueAdmission
{
    // Builds an ordinary owner registration request only. Does not register it, grant it, add aliases or rename IDs.
    // Invalid literal IDs fail the entire candidate rather than the genuine IAM service's skip-invalid behavior.
    public static IReadOnlyList<RegisterPermissionsRequest> PrepareRequests(IReadOnlyList<SourcePermission> sourcePermissions)
    {
        if (sourcePermissions.Count is < 1 or > 128)
        {
            throw new InvalidDataException("Finite source catalogue required.");
        }
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in sourcePermissions)
        {
            if (permission.OwnerCommit.Length != 40 || permission.OwnerCommit.Any(character => !"0123456789abcdef".Contains(character))
                || string.IsNullOrWhiteSpace(permission.SourcePath) || permission.SourceLine <= 0 || string.IsNullOrWhiteSpace(permission.Description)
                || !ids.Add(permission.PermissionId) || !PermissionFormatValidator.IsValid(permission.PermissionId))
            {
                throw new InvalidDataException("Exact unique source permission must match the genuine three-segment grammar.");
            }
        }
        return sourcePermissions.GroupBy(permission => PermissionFormatValidator.Parse(permission.PermissionId).Service, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new RegisterPermissionsRequest
            {
                ServiceName = group.Key,
                Permissions = group.OrderBy(permission => permission.PermissionId, StringComparer.Ordinal)
                    .Select(permission => new PermissionDto { PermissionId = permission.PermissionId, Description = permission.Description }).ToList(),
            }).ToArray();
    }
}
