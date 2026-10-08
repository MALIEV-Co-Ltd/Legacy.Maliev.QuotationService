extern alias IamApplication;
extern alias IamInfrastructure;

using System.Reflection;
using System.Text.Json;
using FinancialCompositionAdapter;
using FinancialCompositionAdapterControls;
using IamApplication::Maliev.IAMService.Application.Interfaces;
using IamApplication::Maliev.IAMService.Application.Services;
using IamApplication::Maliev.IAMService.Application.Validators;
using IamInfrastructure::Maliev.IAMService.Infrastructure.Persistence;
using IamInfrastructure::Maliev.IAMService.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

// There is no host, provider connection, migration, principal creation or grant in these controls.
var results = new List<string>();
var protocol = Console.Out;
Console.SetOut(TextWriter.Null);
Console.SetError(TextWriter.Null);
try
{
    const string pin = "4fe6642e5674013de9a3672505aec898fdcae0ed";
    SourcePermission Permission(string id) => new(id, "Synthetic grammar witness", pin, "synthetic-control.cs", 1);
    const string valid = "legacy.accounting-financial-ownership.read";
    const string invalid = "legacy.accounting.invoice-financial-ownership.read";
    Require(PermissionFormatValidator.IsValid(valid) && !PermissionFormatValidator.IsValid(invalid));
    results.Add("OriginalThreeSegmentGrammar");
    var request = SourceCatalogueAdmission.PrepareRequests([Permission(valid)]).Single();
    Require(request.ServiceName == "legacy" && request.Permissions.Single().PermissionId == valid);
    results.Add("TypedCataloguePreservesLiteral");
    Refuses(() => SourceCatalogueAdmission.PrepareRequests([Permission(valid), Permission(invalid)]));
    results.Add("FourSegmentRefusesWholeCatalogue");
    Refuses(() => SourceCatalogueAdmission.PrepareRequests([Permission(valid), Permission(valid)]));
    results.Add("DuplicateLiteralRefused");
    Refuses(() => SourceCatalogueAdmission.PrepareRequests([Permission(valid) with { OwnerCommit = "main" }]));
    results.Add("UnpinnedWitnessRefused");
    await RejectSubstitutionAsync(true);
    results.Add("SubstitutedPrincipalServiceRefusedBeforeDatabase");
    await RejectSubstitutionAsync(false);
    results.Add("SubstitutedPrincipalRepositoryRefusedBeforeDatabase");
    Require(NeverInvokeProxy.Calls == 0);
    results.Add("NoSubstitutedDependencyInvoked");
    var physicalColumnControls = await PhysicalColumnControls.RunAsync();
    await protocol.WriteLineAsync(JsonSerializer.Serialize(new
    {
        Passed = results,
        PhysicalColumnControlsPassed = physicalColumnControls,
        PhysicalColumnControlCount = physicalColumnControls.Count,
        PhysicalPostgreSqlObserved = false,
        PhysicalSchemaAccepted = false,
        HostStarted = false,
        DatabaseOpened = false,
        EnrollmentAccepted = false,
        FinancialEightAccepted = false
    }));
    return 0;
}
catch (Exception)
{
    await protocol.WriteLineAsync("{\"ControlFailure\":true}");
    return 1;
}

static void Require(bool value)
{
    if (!value)
    {
        throw new InvalidDataException("Compile control predicate failed.");
    }
}

static void Refuses(Action action)
{
    try
    {
        action();
    }
    catch (InvalidDataException)
    {
        return;
    }
    throw new InvalidDataException("Required catalogue refusal absent.");
}

static async Task RejectSubstitutionAsync(bool substituteService)
{
    var options = new DbContextOptionsBuilder<IAMDbContext>().Options;
    await using var context = new IAMDbContext(options);
    var repository = new PrincipalRepository(context);
    var constructor = typeof(PrincipalService).GetConstructors().Single();
    var arguments = constructor.GetParameters().Select(parameter =>
    {
        if (parameter.ParameterType == typeof(IPrincipalRepository))
        {
            return (object)repository;
        }
        if (parameter.ParameterType == typeof(IConfiguration))
        {
            return new ConfigurationBuilder().Build();
        }
        if (parameter.ParameterType == typeof(Microsoft.Extensions.Logging.ILogger<PrincipalService>))
        {
            return NullLogger<PrincipalService>.Instance;
        }
        return DispatchProxy.Create(parameter.ParameterType, typeof(NeverInvokeProxy));
    }).ToArray();
    var service = (IPrincipalService)constructor.Invoke(arguments);
    var registrations = new ServiceCollection();
    registrations.AddSingleton(context);
    registrations.AddSingleton<IPrincipalService>(substituteService ? DispatchProxy.Create<IPrincipalService, NeverInvokeProxy>() : service);
    registrations.AddSingleton<IPrincipalRepository>(substituteService ? repository : DispatchProxy.Create<IPrincipalRepository, NeverInvokeProxy>());
    await using var provider = registrations.BuildServiceProvider();
    var expected = new OwnedDatabaseExpectation("IAM", new string('a', 32), "unused", 1, 1, DateTimeOffset.UtcNow.AddMinutes(1));
    try
    {
        await OrdinaryOpaquePrincipalEnrollment.EnrollAsync(provider, expected, "service:legacy-auth", new string('0', 64), new string('0', 64), CancellationToken.None);
    }
    catch (InvalidDataException exception) when (exception.Message == "Original runtime DI principal service and repository required.")
    {
        return;
    }
    throw new InvalidDataException("Exact binding refusal absent.");
}

public class NeverInvokeProxy : DispatchProxy
{
    public static int Calls { get; private set; }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        Calls++;
        throw new InvalidOperationException("Substitution must be rejected before dependency invocation.");
    }
}
