using InvoiceCompletionProducerAcceptance;
using Xunit;

namespace Legacy.Maliev.QuotationService.Tests.Tools;

/// <summary>Source-only pure configuration controls; not genuine host or storage acceptance.</summary>
public sealed class CompanionLauncherAdmissionTests
{
    private const string Run = "c821-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private static readonly string File = new Npgsql.NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Port = 5432,
        Database = "c821_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_file",
        Username = "fixture",
        Password = Guid.NewGuid().ToString("N"),
    }.ConnectionString;

    [Fact]
    public void TwelfthDistinctFileDatabaseIsAdmitted()
    {
        CompanionLauncherAdmission.ValidateFileDatabase(Core(), File, Environment(File), Run);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("::1")]
    public void FileAliasOfCorePhysicalDatabaseIsRejected(string host)
    {
        var file = new Npgsql.NpgsqlConnectionStringBuilder(File)
        {
            Host = host,
            Database = "c821_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_core0",
        }.ConnectionString;
        Assert.Throws<InvalidDataException>(() => CompanionLauncherAdmission.ValidateFileDatabase(Core(), file, Environment(file), Run));
    }

    [Theory]
    [InlineData("ConnectionStrings__OtherFileContext")]
    [InlineData("connectionstrings__filedbcontext")]
    [InlineData("ConnectionStrings:FileDbContext")]
    public void FileConsumedKeySubstitutionIsRejected(string key)
    {
        var environment = new Dictionary<string, string> { [key] = File };
        Assert.Throws<InvalidDataException>(() => CompanionLauncherAdmission.ValidateFileDatabase(Core(), File, environment, Run));
    }

    [Fact]
    public void FileRuntimeTargetMismatchIsRejected()
    {
        var other = new Npgsql.NpgsqlConnectionStringBuilder(File) { Database = "c821_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_other" }.ConnectionString;
        Assert.Throws<InvalidDataException>(() => CompanionLauncherAdmission.ValidateFileDatabase(Core(), File, Environment(other), Run));
    }

    [Fact]
    public void NotificationIntentActivationIsRejectedForNoSendProfile()
    {
        var environment = new Dictionary<string, string> { ["Notifications__DeliveryIntentsEnabled"] = "true" };
        Assert.Throws<InvalidDataException>(() => CompanionLauncherAdmission.RequireExact(environment, "Notifications__DeliveryIntentsEnabled", "false"));
    }

    [Fact]
    public void MatchingFrameworkProductionSelectorsAreAdmitted()
    {
        CompanionLauncherAdmission.RequireProductionEnvironment(new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_ENVIRONMENT"] = "Production",
        });
    }

    [Fact]
    public void ConflictingFrameworkEnvironmentSelectorIsRejected()
    {
        var environment = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["DOTNET_ENVIRONMENT"] = "Development",
        };
        Assert.Throws<InvalidDataException>(() => CompanionLauncherAdmission.RequireProductionEnvironment(environment));
    }

    [Fact]
    public void FileHostedSelectorsAreAdmittedAsProcessPrerequisitesOnly()
    {
        CompanionLauncherAdmission.RequireCompanionEnvironment("File", new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "HostedFinancialCompletionAcceptance",
            ["DOTNET_ENVIRONMENT"] = "HostedFinancialCompletionAcceptance",
        });
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void FileOrdinaryEnvironmentIsRejected(string environment)
    {
        Assert.Throws<InvalidDataException>(() => CompanionLauncherAdmission.RequireCompanionEnvironment("File",
            new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = environment }));
    }

    [Fact]
    public void FileConflictingSelectorIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => CompanionLauncherAdmission.RequireCompanionEnvironment("File",
            new Dictionary<string, string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "HostedFinancialCompletionAcceptance",
                ["DOTNET_ENVIRONMENT"] = "Production",
            }));
    }

    [Theory]
    [InlineData("Document")]
    [InlineData("Notification")]
    public void OtherCompanionsKeepProduction(string owner)
    {
        CompanionLauncherAdmission.RequireCompanionEnvironment(owner,
            new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "Production" });
        Assert.Throws<InvalidDataException>(() => CompanionLauncherAdmission.RequireCompanionEnvironment(owner,
            new Dictionary<string, string> { ["ASPNETCORE_ENVIRONMENT"] = "HostedFinancialCompletionAcceptance" }));
    }

    [Fact]
    public void FileMissingSelectorIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => CompanionLauncherAdmission.RequireCompanionEnvironment("File",
            new Dictionary<string, string>()));
    }

    private static Dictionary<string, string> Environment(string connection) => new()
    {
        ["ConnectionStrings__FileDbContext"] = connection,
    };

    private static Dictionary<string, string> Core() => ProducerDatabaseBindings.Roles()
        .Select((role, index) => (role, connection: new Npgsql.NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = 5432,
            Database = $"c821_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa_core{index}",
            Username = "fixture",
            Password = Guid.NewGuid().ToString("N"),
        }.ConnectionString))
        .ToDictionary(value => value.role, value => value.connection, StringComparer.Ordinal);
}
