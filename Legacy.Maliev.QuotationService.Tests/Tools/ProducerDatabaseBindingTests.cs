using InvoiceCompletionProducerAcceptance;

namespace Legacy.Maliev.QuotationService.Tests.Tools;

public sealed class ProducerDatabaseBindingTests
{
    [Theory]
    [InlineData("Auth")]
    [InlineData("Accounting")]
    [InlineData("Quotation")]
    [InlineData("Order")]
    [InlineData("IAM")]
    public void ExactConsumedOwnerContractsPass(string owner)
    {
        var bindings = ProducerDatabaseBindings.Required(owner);
        ProducerDatabaseBindings.Validate(owner, bindings);
        ProducerDatabaseBindings.ValidateEnvironment(owner, bindings, EnvironmentFor(bindings), Isolated());
    }

    [Theory]
    [InlineData("Auth")]
    [InlineData("Accounting")]
    [InlineData("Quotation")]
    [InlineData("Order")]
    [InlineData("IAM")]
    public void DummyKeyCannotStandInForConsumedDatabase(string owner)
    {
        var bindings = ProducerDatabaseBindings.Required(owner);
        var first = bindings.First();
        bindings.Remove(first.Key);
        bindings.Add("ConnectionStrings__Dummy", first.Value);
        Assert.Throws<InvalidDataException>(() => ProducerDatabaseBindings.Validate(owner, bindings));
    }

    [Theory]
    [InlineData("ConnectionStrings__EmployeeIdentity")]
    [InlineData("ConnectionStrings__CustomerIdentity")]
    public void AuthIdentityStoresCannotBeOmitted(string key)
    {
        var bindings = ProducerDatabaseBindings.Required("Auth");
        bindings.Remove(key);
        Assert.Throws<InvalidDataException>(() => ProducerDatabaseBindings.Validate("Auth", bindings));
    }

    [Fact]
    public void ActualConsumedProcessValueMustMatchIsolatedBackend()
    {
        var bindings = ProducerDatabaseBindings.Required("Auth");
        var environment = EnvironmentFor(bindings);
        environment["ConnectionStrings__EmployeeIdentity"] = "Host=127.0.0.1;Database=foreign;Username=fixture";
        Assert.Throws<InvalidOperationException>(() => ProducerDatabaseBindings.ValidateEnvironment("Auth", bindings, environment, Isolated()));
    }

    [Fact]
    public void CaseVariantConsumedProcessKeysAreAmbiguous()
    {
        var bindings = ProducerDatabaseBindings.Required("Auth");
        var environment = EnvironmentFor(bindings);
        environment.Add("CONNECTIONSTRINGS__EMPLOYEEIDENTITY", environment["ConnectionStrings__EmployeeIdentity"]);
        Assert.Throws<InvalidDataException>(() => ProducerDatabaseBindings.ValidateEnvironment("Auth", bindings, environment, Isolated()));
    }

    [Fact]
    public void ColonAliasConsumedProcessKeysAreAmbiguous()
    {
        var bindings = ProducerDatabaseBindings.Required("Auth");
        var environment = EnvironmentFor(bindings);
        environment.Add("ConnectionStrings:EmployeeIdentity", environment["ConnectionStrings__EmployeeIdentity"]);
        Assert.Throws<InvalidDataException>(() => ProducerDatabaseBindings.ValidateEnvironment("Auth", bindings, environment, Isolated()));
    }

    [Fact]
    public void IdentityRolesCannotBeInterchanged()
    {
        var bindings = ProducerDatabaseBindings.Required("Auth");
        bindings["ConnectionStrings__EmployeeIdentity"] = "AUTH_CUSTOMER_IDENTITY";
        Assert.Throws<InvalidDataException>(() => ProducerDatabaseBindings.Validate("Auth", bindings));
    }

    [Fact]
    public void MissingActualConsumedKeyFailsDespiteDummyProcessKey()
    {
        var bindings = ProducerDatabaseBindings.Required("Auth");
        var environment = EnvironmentFor(bindings);
        var value = environment["ConnectionStrings__EmployeeIdentity"];
        environment.Remove("ConnectionStrings__EmployeeIdentity");
        environment.Add("ConnectionStrings__Dummy", value);
        Assert.Throws<InvalidDataException>(() => ProducerDatabaseBindings.ValidateEnvironment("Auth", bindings, environment, Isolated()));
    }

    [Fact]
    public void SameServerWithDifferentDatabaseNamesIsSeparate()
    {
        ProducerDatabaseBindings.ValidateSeparateDatabases(Isolated());
    }

    [Fact]
    public void DifferentCredentialsDoNotSeparateDuplicatePhysicalDatabase()
    {
        var isolated = Isolated();
        isolated["AUTH_EMPLOYEE_IDENTITY"] = isolated["AUTH_CUSTOMER_IDENTITY"].Replace("Username=fixture", "Username=other", StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ProducerDatabaseBindings.ValidateSeparateDatabases(isolated));
    }

    [Theory]
    [InlineData("127.0.0.1", "localhost")]
    [InlineData("127.0.0.1", "::1")]
    [InlineData("localhost", "::1")]
    public void LoopbackAliasesCannotSeparateDuplicatePhysicalDatabase(string firstHost, string secondHost)
    {
        var isolated = Isolated();
        isolated["AUTH_CUSTOMER_IDENTITY"] = $"Host={firstHost};Port=5432;Database=c821_same;Username=fixture";
        isolated["AUTH_EMPLOYEE_IDENTITY"] = $"Host={secondHost};Port=5432;Database=c821_same;Username=fixture";
        Assert.Throws<InvalidDataException>(() => ProducerDatabaseBindings.ValidateSeparateDatabases(isolated));
    }

    [Fact]
    public void SeparateServerPortsMayUseTheSameDatabaseName()
    {
        var isolated = Isolated();
        isolated["AUTH_CUSTOMER_IDENTITY"] = "Host=127.0.0.1;Port=5432;Database=c821_same;Username=fixture";
        isolated["AUTH_EMPLOYEE_IDENTITY"] = "Host=localhost;Port=5433;Database=c821_same;Username=fixture";
        ProducerDatabaseBindings.ValidateSeparateDatabases(isolated);
    }

    [Fact]
    public void NormalDotnetAbsoluteDllLaunchPasses()
    {
        var dll = Path.GetFullPath("fixture/Auth.dll");
        ProducerDatabaseBindings.ValidateLaunchArguments(["dotnet", dll], dll, "/usr/share/dotnet/dotnet");
    }

    [Theory]
    [InlineData("--ConnectionStrings:EmployeeIdentity=foreign")]
    [InlineData("ConnectionStrings:EmployeeIdentity=foreign")]
    [InlineData("/ConnectionStrings:EmployeeIdentity=foreign")]
    [InlineData("--Services:Order:BaseUrl=https://foreign.invalid")]
    [InlineData("Services:Order:BaseUrl=https://foreign.invalid")]
    [InlineData("/Services:Order:BaseUrl=https://foreign.invalid")]
    [InlineData("--urls=https://foreign.invalid")]
    [InlineData("--configuration=foreign")]
    public void AllConfigurationArgumentsRejectBeforeProducerCalls(string argument)
    {
        var dll = Path.GetFullPath("fixture/Auth.dll");
        Assert.Throws<InvalidDataException>(() => ProducerDatabaseBindings.ValidateLaunchArguments(
            ["dotnet", dll, argument], dll, "/usr/share/dotnet/dotnet"));
    }

    private static Dictionary<string, string> Isolated() => ProducerDatabaseBindings.Roles().ToDictionary(role => role,
        role => $"Host=127.0.0.1;Database=c821_test_{role.ToLowerInvariant()};Username=fixture", StringComparer.Ordinal);
    private static Dictionary<string, string> EnvironmentFor(Dictionary<string, string> bindings)
    {
        var isolated = Isolated();
        return bindings.ToDictionary(binding => binding.Key, binding => isolated[binding.Value], StringComparer.Ordinal);
    }
}
