extern alias IamApplication;
extern alias IamInfrastructure;

using System.Data;
using IamApplication::Maliev.IAMService.Application.Services;
using IamApplication::Maliev.IAMService.Application.Interfaces;
using IamInfrastructure::Maliev.IAMService.Infrastructure.Persistence;
using IamInfrastructure::Maliev.IAMService.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FinancialCompositionAdapter;

// Returned acquisition values stay private in the parent's lease; no logging or public receipt serializer here.
public sealed record EnrolledOpaquePrincipal(string Subject, Guid PersistedPrincipalId);

public static class OrdinaryOpaquePrincipalEnrollment
{
    // Exact callers of the selected financial source. This is not a permission/binding catalogue.
    private static readonly string[] Subjects =
    [
        "service:legacy-intranet", "service:legacy-accounting", "service:legacy-auth", "service:legacy-quotation",
    ];

    public static async Task<EnrolledOpaquePrincipal> EnrollAsync(
        IServiceProvider exclusiveRuntimeScope, OwnedDatabaseExpectation expected, string subject,
        string qualifiedIamAssemblySha256, string qualifiedIamApplicationSha256, CancellationToken cancellationToken)
    {
        if (expected.Role != "IAM" || !Subjects.Contains(subject, StringComparer.Ordinal))
        {
            throw new InvalidDataException("Exact ordinary financial caller and IAM database role required.");
        }
        var context = exclusiveRuntimeScope.GetRequiredService<IAMDbContext>();
        var principals = exclusiveRuntimeScope.GetRequiredService<IPrincipalService>();
        if (principals.GetType() != typeof(PrincipalService)
            || exclusiveRuntimeScope.GetRequiredService<IPrincipalRepository>().GetType() != typeof(PrincipalRepository))
        {
            throw new InvalidDataException("Original runtime DI principal service and repository required.");
        }
        OwnedBusinessSchema.VerifyQualifiedAssembly(typeof(PrincipalService).Assembly, qualifiedIamApplicationSha256);
        await OwnedBusinessSchema.ObserveAsync(context, expected, qualifiedIamAssemblySha256, cancellationToken);
        using var deadline = OwnedBusinessSchema.Deadline(expected, cancellationToken);
        var token = deadline.Token;
        var email = subject.ToLowerInvariant() + "@serviceaccount.maliev.local";
        Guid acquiredId;

        // The real service generates the ID and persists through its runtime-scoped repository.
        // No direct entity insertion, API-key creation, workload provisioning, role or permission grant occurs.
        await using (var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable, token))
        {
            if (await context.Principals.AsNoTracking().AnyAsync(principal => principal.Email == email, token))
            {
                throw new InvalidDataException("Synthetic enrollment must start with an absent exact principal email.");
            }
            var created = await principals.CreateAsync("service_account", email, null, null, token);
            acquiredId = created.PrincipalId;
            var persisted = await context.Principals.AsNoTracking().SingleAsync(principal => principal.Email == email, token);
            if (acquiredId == Guid.Empty || persisted.PrincipalId != acquiredId || persisted.PrincipalType != "service_account"
                || !persisted.IsActive || persisted.WorkloadId is not null || persisted.LinkedService is not null || persisted.LinkedEntityId is not null
                || await context.ServiceAccountApiKeys.AnyAsync(key => key.PrincipalId == acquiredId, token)
                || await context.PrincipalRoleBindings.AnyAsync(binding => binding.PrincipalId == acquiredId, token)
                || await context.PrincipalPermissionBindings.AnyAsync(binding => binding.PrincipalId == acquiredId, token))
            {
                throw new InvalidDataException("Ordinary server-owned persisted enrollment shape differs or has authority.");
            }
            await transaction.CommitAsync(token);
        }

        // Resolve after commit: the original resolver caches answers and must never cache a rolled-back row.
        await OwnedBusinessSchema.VerifyDatabaseAsync(context, expected, token);
        if (await principals.ResolvePrincipalIdAsync(subject, token) != acquiredId
            || await principals.ResolvePrincipalIdAsync(email, token) != acquiredId
            || (await principals.GetByIdAsync(acquiredId, token))?.Email != email)
        {
            throw new InvalidDataException("Unchanged opaque subject does not resolve to its actual persisted principal.");
        }
        return new(subject, acquiredId);
    }
}
