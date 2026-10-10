using System.Text.RegularExpressions;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.QuotationService.Tests.Workflows;

public sealed class WorkflowContractTests
{
    private static readonly string Workflow = File.ReadAllText(FindRepositoryFile(".github", "workflows", "_build-and-test.yml"));
    private static readonly string ApiProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.QuotationService.Api", "Legacy.Maliev.QuotationService.Api.csproj"));
    private static readonly string DataProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.QuotationService.Data", "Legacy.Maliev.QuotationService.Data.csproj"));
    private static readonly string MigrationRunnerProject = File.ReadAllText(
        FindRepositoryFile("Legacy.Maliev.QuotationService.MigrationRunner", "Legacy.Maliev.QuotationService.MigrationRunner.csproj"));

    [Fact]
    public void BuildAndTest_SatisfiesStructuralContract()
    {
        WorkflowContractValidator.Validate(Workflow);
    }

    [Theory]
    [InlineData("  qualification-authority-joined:", "  unexpected-job:")]
    [InlineData("    name: qualification-authority-joined", "    if: false\n    name: qualification-authority-joined")]
    [InlineData("    timeout-minutes: 20", "    continue-on-error: true\n    timeout-minutes: 20")]
    [InlineData("ref: 8cdb634b3b0abdf18b9b826a0948dbfd98c66ea0", "ref: main")]
    [InlineData("ref: 5c5f9479313710fa576f83d3b396442997a2fcf4", "ref: main")]
    [InlineData("path: TestResults/.joined-auth/.dependencies/Legacy.Maliev.ServiceDefaults", "path: TestResults/.bridge-dependencies/Legacy.Maliev.ServiceDefaults")]
    [InlineData("dotnet-version: 10.0.x", "dotnet-version: 9.0.x")]
    [InlineData("shell: pwsh", "shell: bash")]
    [InlineData("run: ./scripts/run_qualification_authority_joined.ps1 -RepositoryPath $env:GITHUB_WORKSPACE", "run: echo skipped")]
    public void BuildAndTest_RejectsJoinedAuthorityGateBypass(string original, string replacement) =>
        AssertMutationRejected(original, replacement);

    [Theory]
    [InlineData("name: Prove actual qualification DTO and controller serializer wire", "name: Copied qualification serializer")]
    [InlineData("        timeout-minutes: 2\n", "        timeout-minutes: 2\n        continue-on-error: true\n")]
    [InlineData("FullyQualifiedName~QualificationOutcomeWireSourceTests", "FullyQualifiedName~NoWireSourceCases")]
    [InlineData("python3 -B scripts/check_qualification_wire_results.py", "echo native evidence skipped")]
    [InlineData("dotnet tools/QualificationOutcomeWireSource/bin/Release/net10.0/QualificationOutcomeWireSource.dll", "dotnet CopiedDtoHarness.dll")]
    [InlineData("name: qualification-source-wire-${{ github.sha }}", "name: unbound-wire")]
    [InlineData("            TestResults/QualificationWire/empty.json\n", "            TestResults/**\n")]
    [InlineData("always() && hashFiles('TestResults/QualificationWire/qualification-wire.trx') != ''", "success()")]
    public void BuildAndTest_RejectsWireSourceGateBypass(string original, string replacement) =>
        AssertMutationRejected(original, replacement);

    [Theory]
    [InlineData("python3 -B -m unittest discover -s tools/InvoiceCompletionProducerAcceptance/companion -p 'test_*.py'", "echo companion controls skipped")]
    [InlineData("FullyQualifiedName~HostedV4SignedReadVerifierTests", "FullyQualifiedName~NoSignerControls")]
    [InlineData("FullyQualifiedName~CompanionLauncherAdmissionTests", "FullyQualifiedName~NoAdmissionControls")]
    [InlineData("python3 -B scripts/check_companion_focused_results.py", "echo native companion receipt skipped")]
    [InlineData("            TestResults/CompanionSigning/companion-signing.trx\n", "            TestResults/CompanionSigning/**\n")]
    public void BuildAndTest_RejectsCompanionNativeGateBypass(string original, string replacement) =>
        AssertMutationRejected(original, replacement);

    [Fact]
    public void ApiUsesPinnedSharedRequestFailureTracing()
    {
        var program = File.ReadAllText(FindRepositoryFile("Legacy.Maliev.QuotationService.Api", "Program.cs"));

        Assert.Contains("builder.AddStandardMiddleware(", program, StringComparison.Ordinal);
        Assert.Contains("app.UseStandardMiddleware()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("app.UseExceptionHandler(", program, StringComparison.Ordinal);
        Assert.Contains("ref: f72be151917cb3962380971418c187e3df563f8d", Workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void DependabotConfiguration_ScansOnlyIndependentlyResolvableProjectDirectories()
    {
        var source = File.ReadAllText(FindRepositoryFile(".github", "dependabot.yml"));
        var yaml = new YamlStream();
        yaml.Load(new StringReader(source));

        var root = Assert.IsType<YamlMappingNode>(Assert.Single(yaml.Documents).RootNode);
        var updates = Assert.IsType<YamlSequenceNode>(ReadNode(root, "updates"));
        var nuget = updates.Children
            .Select(Assert.IsType<YamlMappingNode>)
            .Single(update => ReadScalar(update, "package-ecosystem") == "nuget");
        var directories = Assert.IsType<YamlSequenceNode>(ReadNode(nuget, "directories"));

        Assert.Equal(
            [
                "/Legacy.Maliev.QuotationService.Application",
                "/Legacy.Maliev.QuotationService.Data",
                "/Legacy.Maliev.QuotationService.Domain",
                "/Legacy.Maliev.QuotationService.MigrationRunner",
            ],
            directories.Children.Select(Assert.IsType<YamlScalarNode>).Select(node => node.Value));
        Assert.False(nuget.Children.ContainsKey(new YamlScalarNode("directory")));
        Assert.False(nuget.Children.ContainsKey(new YamlScalarNode("exclude-paths")));
    }

    [Fact]
    public void DependabotConfiguration_AllowsCoordinatedEfUpdatesAndDefersRedisAdvisory()
    {
        var source = File.ReadAllText(FindRepositoryFile(".github", "dependabot.yml"));

        foreach (var dependency in new[]
                 {
                     "Microsoft.EntityFrameworkCore",
                     "Microsoft.EntityFrameworkCore.Abstractions",
                     "Microsoft.EntityFrameworkCore.Design",
                     "Microsoft.EntityFrameworkCore.Relational",
                     "Npgsql.EntityFrameworkCore.PostgreSQL",
                 })
        {
            Assert.DoesNotContain($"dependency-name: {dependency}", source, StringComparison.Ordinal);
        }

        Assert.Contains("dependency-name: StackExchange.Redis", source, StringComparison.Ordinal);
        Assert.Contains("versions: [\"3.1.31\"]", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAndTest_RejectsSharedActionMainWithPinnedShaComment()
    {
        AssertMutationRejected(
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@53892c362a30130f582c40da7525e44f11474e8e",
            "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@main # 53892c362a30130f582c40da7525e44f11474e8e");
    }

    [Fact]
    public void BuildAndTest_RejectsCommentedDependencySha()
    {
        AssertMutationRejected(
            "ref: f72be151917cb3962380971418c187e3df563f8d",
            "ref: main # f72be151917cb3962380971418c187e3df563f8d");
    }

    [Fact]
    public void ApiProject_UsesOnlyLegacyServiceDefaults()
    {
        Assert.Contains("Legacy.Maliev.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Maliev.Aspire\\Maliev.Aspire.ServiceDefaults", ApiProject, StringComparison.Ordinal);
        Assert.DoesNotContain("Include=\"Maliev.Aspire.ServiceDefaults\"", ApiProject, StringComparison.Ordinal);
    }

    [Fact]
    public void EfDesignDependency_IsOwnedByDataProjectOnly()
    {
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore.Design", ApiProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.EntityFrameworkCore.Design", DataProject, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationRunner_UsesCoordinatedEfAndNpgsqlRuntimeGraph()
    {
        Assert.Contains("Microsoft.EntityFrameworkCore\" Version=\"10.0.12", MigrationRunnerProject, StringComparison.Ordinal);
        Assert.Contains("Microsoft.EntityFrameworkCore.Relational\" Version=\"10.0.12", MigrationRunnerProject, StringComparison.Ordinal);
        Assert.Contains("Npgsql.EntityFrameworkCore.PostgreSQL\" Version=\"10.0.3", MigrationRunnerProject, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAndTest_RejectsJobPermissionEscalation()
    {
        AssertMutationRejected(
            "  validate:\n    name: validate",
            "  validate:\n    permissions:\n      contents: write\n    name: validate");
    }

    [Fact]
    public void BuildAndTest_RejectsSecretReferenceAnywhere()
    {
        var mutated = $"{Workflow}\n# ${{{{ secrets.X }}}}\n";

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
    }

    [Theory]
    [InlineData("${{secrets.X}}")]
    [InlineData("${{ secrets['X'] }}")]
    public void BuildAndTest_RejectsSecretExpressionInJobEnvironment(string expression)
    {
        AssertMutationRejected(
            "    env:\n      MalievWorkspaceRoot: ${{ github.workspace }}/.dependencies",
            $"    env:\n      MalievWorkspaceRoot: ${{{{ github.workspace }}}}/.dependencies\n      REVIEW_TOKEN: {expression}");
    }

    [Fact]
    public void BuildAndTest_RejectsWhitespaceObfuscatedRestoreCommand()
    {
        AssertMutationRejected(
            "          solution: Legacy.Maliev.QuotationService.slnx",
            "          solution: Legacy.Maliev.QuotationService.slnx\n      - run: dotnet  restore Legacy.Maliev.QuotationService.slnx");
    }

    [Fact]
    public void BuildAndTest_RejectsMissingLocalDependencyOptIn()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            string.Empty);
    }

    [Fact]
    public void BuildAndTest_RejectsReservedGitHubActionsOverride()
    {
        AssertMutationRejected(
            "          use-local-maliev-dependencies: 'true'\n",
            "          use-local-maliev-dependencies: 'true'\n        env:\n          GITHUB_ACTIONS: 'false'\n");
    }

    [Fact]
    public void BuildAndTest_RejectsCoverageThresholdReduction()
    {
        AssertMutationRejected("--minimum 80", "--minimum 79");
    }

    [Fact]
    public void BuildAndTest_RejectsCoverageCollectorRemoval()
    {
        AssertMutationRejected("--collect 'XPlat Code Coverage'", "--collect 'Code Coverage'");
    }

    [Fact]
    public void BuildAndTest_RejectsRawCoverageGateRemoval() =>
        AssertMutationRejected("--minimum 80 --raw", "--minimum 80");

    [Fact]
    public void BuildAndTest_RejectsRecursiveTrxAttachmentCopySelection() =>
        AssertMutationRejected("find TestResults/CoverageGate -mindepth 2 -maxdepth 2", "find TestResults/CoverageGate");

    [Theory]
    [InlineData("retention-days: 7", "retention-days: 90")]
    [InlineData("include-hidden-files: false", "include-hidden-files: true")]
    [InlineData("TestResults/CoverageGate/*/coverage.cobertura.xml", "TestResults/**")]
    [InlineData("always() && (hashFiles('TestResults/CoverageGate/quotation-complete-coverage.trx') != '' || hashFiles('TestResults/C821Verifier/c821-verifier.trx', 'TestResults/C821Recipient/c821-recipient.trx') != '')", "success()")]
    public void BuildAndTest_RejectsCoverageEvidenceContractMutation(string original, string replacement) =>
        AssertMutationRejected(original, replacement);

    [Fact]
    public void CoverageSettings_RetainGeneratedLinesAndAutomaticProperties()
    {
        var settings = System.Xml.Linq.XDocument.Load(
            FindRepositoryFile("Legacy.Maliev.QuotationService.Tests", "coverage.runsettings"));
        var collector = Assert.Single(settings.Descendants("DataCollector"));
        Assert.Equal("XPlat Code Coverage", collector.Attribute("friendlyName")?.Value);
        var configuration = collector.Element("Configuration")!;
        Assert.Equal("[Legacy.Maliev.QuotationService.*]*", configuration.Element("Include")?.Value);
        Assert.Equal("[*.Tests]*", configuration.Element("Exclude")?.Value);
        Assert.Equal(string.Empty, configuration.Element("ExcludeByAttribute")?.Value);
        Assert.Equal("false", configuration.Element("SkipAutoProps")?.Value);
    }


    [Theory]
    [InlineData("validate", "remove")]
    [InlineData("validate", "reorder")]
    [InlineData("validate", "duplicate")]
    [InlineData("validate", "command")]
    [InlineData("validate", "timeout")]
    [InlineData("validate", "step-env")]
    [InlineData("validate", "job-env")]
    [InlineData("validate", "ryuk-digest")]
    [InlineData("validate", "ryuk-registry")]
    [InlineData("validate", "original-pin")]
    [InlineData("validate", "original-action")]
    [InlineData("validate", "secret")]
    [InlineData("validate", "cleanup-if")]
    [InlineData("validate", "upload-action")]
    [InlineData("qualification-authority-joined", "remove")]
    [InlineData("qualification-authority-joined", "reorder")]
    [InlineData("qualification-authority-joined", "duplicate")]
    [InlineData("qualification-authority-joined", "command")]
    [InlineData("qualification-authority-joined", "timeout")]
    [InlineData("qualification-authority-joined", "step-env")]
    [InlineData("qualification-authority-joined", "job-env")]
    [InlineData("qualification-authority-joined", "ryuk-digest")]
    [InlineData("qualification-authority-joined", "ryuk-registry")]
    [InlineData("qualification-authority-joined", "original-pin")]
    [InlineData("qualification-authority-joined", "original-action")]
    [InlineData("qualification-authority-joined", "secret")]
    [InlineData("qualification-authority-joined", "cleanup-if")]
    [InlineData("qualification-authority-joined", "upload-action")]
    public void BuildAndTest_RejectsRegistryPrerequisiteBypass(string jobName, string mutant)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(Workflow));
        var root = Assert.IsType<YamlMappingNode>(yaml.Documents[0].RootNode);
        var jobs = Assert.IsType<YamlMappingNode>(ReadNode(root, "jobs"));
        var job = Assert.IsType<YamlMappingNode>(ReadNode(jobs, jobName));
        var steps = Assert.IsType<YamlSequenceNode>(ReadNode(job, "steps"));
        var controlsIndex = jobName == "validate" ? 4 : 7;
        var preloadIndex = controlsIndex + 1;
        var cleanupIndex = jobName == "validate" ? 13 : 10;
        var uploadIndex = cleanupIndex + 1;
        var preload = Assert.IsType<YamlMappingNode>(steps.Children[preloadIndex]);
        var environment = Assert.IsType<YamlMappingNode>(ReadNode(job, "env"));
        switch (mutant)
        {
            case "remove": steps.Children.RemoveAt(preloadIndex); break;
            case "reorder":
                steps.Children.RemoveAt(preloadIndex);
                steps.Children.Insert(controlsIndex, preload);
                break;
            case "duplicate": steps.Children.Insert(preloadIndex, preload); break;
            case "command": preload.Children[new YamlScalarNode("run")] = new YamlScalarNode("echo skipped"); break;
            case "timeout": preload.Children[new YamlScalarNode("timeout-minutes")] = new YamlScalarNode("60"); break;
            case "step-env":
                var unexpectedEnvironment = new YamlMappingNode();
                unexpectedEnvironment.Add(new YamlScalarNode("UNREVIEWED"), new YamlScalarNode("true"));
                preload.Children.Add(new YamlScalarNode("env"), unexpectedEnvironment);
                break;
            case "job-env": environment.Children.Add(new YamlScalarNode("UNREVIEWED"), new YamlScalarNode("true")); break;
            case "ryuk-digest": environment.Children[new YamlScalarNode("TESTCONTAINERS_RYUK_CONTAINER_IMAGE")] = new YamlScalarNode("ghcr.io/testcontainers/ryuk@sha256:" + new string('0', 64)); break;
            case "ryuk-registry": environment.Children[new YamlScalarNode("TESTCONTAINERS_RYUK_CONTAINER_IMAGE")] = new YamlScalarNode("foreign.invalid/testcontainers/ryuk@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0"); break;
            case "original-pin":
                var checkout = Assert.IsType<YamlMappingNode>(steps.Children[1]);
                Assert.IsType<YamlMappingNode>(ReadNode(checkout, "with")).Children[new YamlScalarNode("ref")] = new YamlScalarNode("main");
                break;
            case "original-action": Assert.IsType<YamlMappingNode>(steps.Children[0]).Children[new YamlScalarNode("uses")] = new YamlScalarNode("actions/checkout@main"); break;
            case "secret": environment.Children[new YamlScalarNode("TESTCONTAINERS_RYUK_CONTAINER_IMAGE")] = new YamlScalarNode("${{ secrets.X }}"); break;
            case "cleanup-if": Assert.IsType<YamlMappingNode>(steps.Children[cleanupIndex]).Children[new YamlScalarNode("if")] = new YamlScalarNode("success()"); break;
            case "upload-action": Assert.IsType<YamlMappingNode>(steps.Children[uploadIndex]).Children[new YamlScalarNode("uses")] = new YamlScalarNode("actions/upload-artifact@main"); break;
            default: throw new InvalidOperationException("Unexpected mutation.");
        }

        using var serialized = new StringWriter();
        yaml.Save(serialized, assignAnchors: false);
        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(serialized.ToString()));
    }

    private static void AssertMutationRejected(string original, string replacement)
    {
        Assert.Contains(original, Workflow, StringComparison.Ordinal);
        var mutated = Workflow.Replace(original, replacement, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(mutated));
    }

    private static YamlNode ReadNode(YamlMappingNode mapping, string key)
    {
        return mapping.Children[new YamlScalarNode(key)];
    }

    private static string ReadScalar(YamlMappingNode mapping, string key)
    {
        return Assert.IsType<YamlScalarNode>(ReadNode(mapping, key)).Value
            ?? throw new InvalidOperationException($"Expected '{key}' to have a scalar value.");
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{Path.Combine(segments)}'.");
    }
}

internal static partial class WorkflowContractValidator
{

    private const string RyukImage = "ghcr.io/testcontainers/ryuk@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0";
    private const string RegistryControls = """
        python3 -B -c "from pathlib import Path; [compile(p.read_bytes(),str(p),'exec') for p in (Path('scripts/registry_preload/preload_images.py'),Path('scripts/tests/test_registry_preload.py'))]"
        python3 -B -m unittest discover -s scripts/tests -p test_registry_preload.py
        python3 -B -O -m unittest discover -s scripts/tests -p test_registry_preload.py
        """;

    private static void ValidateRegistryEnvironment(YamlMappingNode job, bool workspace)
    {
        var environment = RequireMapping(job, "env");
        if (environment.Children.Count != (workspace ? 2 : 1))
            throw new InvalidOperationException("Job environment must contain only its exact reviewed registry/workspace settings.");
        RequireScalarValue(environment, "TESTCONTAINERS_RYUK_CONTAINER_IMAGE", RyukImage);
        if (workspace) RequireScalarValue(environment, "MalievWorkspaceRoot", "${{ github.workspace }}/.dependencies");
    }

    private static void ValidateRegistrySteps(YamlSequenceNode steps, int controls, int preload, int cleanup, int upload)
    {
        ValidateScriptStep(steps.Children[controls], "Compile and test registry prerequisite controls",
            RegistryControls, expectedTimeout: "2");
        ValidateScriptStep(steps.Children[preload], "Prepare exact official fixture images",
            "python3 -B scripts/registry_preload/preload_images.py", expectedTimeout: "5");
        var release = RequireMapping(steps.Children[cleanup], "registry cleanup");
        if (!release.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["name", "if", "timeout-minutes", "shell", "run"]))
            throw new InvalidOperationException("Registry cleanup must have only exact reviewed keys.");
        RequireScalarValue(release, "name", "Release exact owned image references");
        RequireScalarValue(release, "if", "always()");
        RequireScalarValue(release, "timeout-minutes", "2");
        RequireScalarValue(release, "shell", "bash");
        RequireScalarValue(release, "run", "python3 -B scripts/registry_preload/preload_images.py cleanup");
        var evidence = RequireMapping(steps.Children[upload], "registry custody evidence");
        if (!evidence.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["name", "if", "uses", "with"]))
            throw new InvalidOperationException("Registry custody upload must have only exact reviewed keys.");
        RequireScalarValue(evidence, "name", "Retain registry custody evidence");
        RequireScalarValue(evidence, "if", "always()");
        RequireScalarValue(evidence, "uses", "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a");
        var inputs = RequireMapping(evidence, "with");
        if (inputs.Children.Count != 5) throw new InvalidOperationException("Registry custody upload requires exactly five inputs.");
        RequireScalarValue(inputs, "name", "quotation-registry-custody-${{ github.job }}-${{ github.sha }}");
        RequireScalarValue(inputs, "path", "${{ runner.temp }}/quotation-registry-preload.json");
        RequireScalarValue(inputs, "if-no-files-found", "ignore");
        RequireScalarValue(inputs, "retention-days", "7");
        RequireScalarValue(inputs, "overwrite", "false");
    }

    private const string CheckoutAction = "actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1";
    private const string SharedValidationAction = "MALIEV-Co-Ltd/Legacy.Maliev.Workflows/actions/dotnet-validate@53892c362a30130f582c40da7525e44f11474e8e";
    private const string CoverageProof = """
        python3 -m unittest discover -s scripts/tests -p 'test_*.py'
        python3 -B -m unittest discover -s tools/InvoiceCompletionProducerAcceptance/companion -p 'test_*.py'
        """;
    private const string QualificationWireCollection = """
        dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
          --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
          --filter 'FullyQualifiedName~QualificationOutcomeWireSourceTests' \
          --results-directory TestResults/QualificationWire --logger 'trx;LogFileName=qualification-wire.trx'
        python3 -B scripts/check_qualification_wire_results.py
        dotnet tools/QualificationOutcomeWireSource/bin/Release/net10.0/QualificationOutcomeWireSource.dll "$GITHUB_WORKSPACE"
        """;
    private const string CoverageCollection = """
        dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
          --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
          --filter 'FullyQualifiedName~QuotationInvoiceCapabilityVerifierTests' \
          --results-directory TestResults/C821Verifier --logger 'trx;LogFileName=c821-verifier.trx'
        dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
          --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
          --filter 'FullyQualifiedName~QuotationInvoiceCapabilityHttpTests' \
          --results-directory TestResults/C821Recipient --logger 'trx;LogFileName=c821-recipient.trx'
        python3 scripts/check_c821_focused_results.py
        dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
          --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
          --filter 'FullyQualifiedName~CompanionLauncherAdmissionTests' \
          --results-directory TestResults/CompanionAdmission --logger 'trx;LogFileName=companion-admission.trx'
        dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
          --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
          --filter 'FullyQualifiedName~HostedV4SignedReadVerifierTests' \
          --results-directory TestResults/CompanionSigning --logger 'trx;LogFileName=companion-signing.trx'
        python3 -B scripts/check_companion_focused_results.py
        dotnet test tools/HostedStorageFront.Tests/HostedStorageFront.Tests.csproj \
          --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
          --results-directory TestResults/StorageFront --logger 'trx;LogFileName=storage-front.trx'
        python3 -B scripts/check_storage_front_results.py
        dotnet test tools/FinancialCompletionEvidence.Tests/FinancialCompletionEvidence.Tests.csproj \
          --configuration Release --no-build --no-restore -p:GITHUB_ACTIONS=false \
          --results-directory TestResults/FinancialCompletion --logger 'trx;LogFileName=financial-completion.trx'
        python3 -B scripts/check_financial_completion_results.py
        dotnet test Legacy.Maliev.QuotationService.Tests/Legacy.Maliev.QuotationService.Tests.csproj \
          --configuration Release --no-build --no-restore \
          -p:GITHUB_ACTIONS=false --collect 'XPlat Code Coverage' --results-directory TestResults/CoverageGate \
          --settings Legacy.Maliev.QuotationService.Tests/coverage.runsettings \
          --logger 'trx;LogFileName=quotation-complete-coverage.trx'
        """;
    private const string CoverageEnforcement = """
        mapfile -t reports < <(find TestResults/CoverageGate -mindepth 2 -maxdepth 2 -type f -name coverage.cobertura.xml)
        if [[ "${#reports[@]}" -ne 1 ]]; then
          echo "Expected exactly one Cobertura report, found ${#reports[@]}." >&2
          exit 1
        fi
        python3 scripts/check_owned_coverage.py "${reports[0]}" --minimum 80
        python3 scripts/check_owned_coverage.py "${reports[0]}" --minimum 80 --raw
        """;

    public static void Validate(string workflow)
    {
        if (SecretExpression().IsMatch(workflow))
        {
            throw new InvalidOperationException("Workflow must not reference secrets.");
        }

        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(workflow));
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Workflow must be valid YAML.", exception);
        }

        if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new InvalidOperationException("Workflow must contain exactly one mapping document.");
        }

        var workflowPermissions = RequireExactReadOnlyPermissions(RequireMapping(root, "permissions"), "workflow");
        var jobs = RequireMapping(root, "jobs");
        if (jobs.Children.Count != 2 || !jobs.Children.Keys.Select(RequireScalar)
            .ToHashSet(StringComparer.Ordinal).SetEquals(["validate", "qualification-authority-joined"]))
        {
            throw new InvalidOperationException("Workflow must define exactly validate and qualification-authority-joined jobs.");
        }

        ValidateJoinedAuthorityJob(RequireMapping(jobs, "qualification-authority-joined"));

        var validateJob = RequireMapping(jobs, "validate");
        var jobPermissionsNode = GetOptional(validateJob, "permissions");
        var effectiveJobPermissions = jobPermissionsNode is null
            ? workflowPermissions
            : RequireExactReadOnlyPermissions(RequireMapping(jobPermissionsNode, "jobs.validate.permissions"), "validate job");
        if (!effectiveJobPermissions.SequenceEqual(workflowPermissions, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Validate job permissions must not differ from workflow permissions.");
        }

        var validateKeys = new HashSet<string>(["name", "runs-on", "timeout-minutes", "env", "steps"], StringComparer.Ordinal);
        if (jobPermissionsNode is not null) validateKeys.Add("permissions");
        if (!validateJob.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal).SetEquals(validateKeys))
            throw new InvalidOperationException("Validate job must contain only exact reviewed settings.");
        RequireScalarValue(validateJob, "name", "validate");
        RequireScalarValue(validateJob, "runs-on", "ubuntu-latest");
        RequireScalarValue(validateJob, "timeout-minutes", "30");
        ValidateRegistryEnvironment(validateJob, workspace: true);
        RejectDuplicatedValidationActionsAndCommands(jobs);

        var steps = RequireSequence(validateJob, "steps");
        if (steps.Children.Count != 15)
        {
            throw new InvalidOperationException("Validate job must contain exactly fifteen caller-owned steps.");
        }

        ValidateStep(
            steps.Children[0],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[1],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.ServiceDefaults",
                ["ref"] = "f72be151917cb3962380971418c187e3df563f8d",
                ["path"] = ".dependencies/Legacy.Maliev.ServiceDefaults",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[2],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.CompatibilityContracts",
                ["ref"] = "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7",
                ["path"] = ".dependencies/Legacy.Maliev.CompatibilityContracts",
                ["persist-credentials"] = "false",
            });
        ValidateStep(
            steps.Children[3],
            CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["repository"] = "MALIEV-Co-Ltd/Legacy.Maliev.AccountingService",
                ["ref"] = "ae0826156b06c34476e95de8c53dfccfcf5a5972",
                ["path"] = ".dependencies/Legacy.Maliev.AccountingService",
                ["persist-credentials"] = "false",
            });
        ValidateRegistrySteps(steps, controls: 4, preload: 5, cleanup: 13, upload: 14);
        ValidateStep(
            steps.Children[6],
            SharedValidationAction,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["solution"] = "Legacy.Maliev.QuotationService.slnx",
                ["use-local-maliev-dependencies"] = "true",
            });
        ValidateScriptStep(steps.Children[7], "Prove coverage gate failure and success behavior", CoverageProof);
        ValidateScriptStep(steps.Children[8], "Prove actual qualification DTO and controller serializer wire",
            QualificationWireCollection, expectedTimeout: "2");
        ValidateScriptStep(steps.Children[9], "Collect QuotationService coverage", CoverageCollection);
        ValidateScriptStep(steps.Children[10], "Enforce 80 percent owned handwritten line coverage", CoverageEnforcement);
        ValidateCoverageEvidenceStep(steps.Children[11]);
        ValidateWireEvidenceStep(steps.Children[12]);
    }

    private static void ValidateCoverageEvidenceStep(YamlNode node)
    {
        var step = RequireMapping(node, "coverage evidence step");
        if (!step.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["name", "if", "uses", "with"]))
            throw new InvalidOperationException("Coverage evidence step must have only its reviewed keys.");
        RequireScalarValue(step, "name", "Retain complete coverage evidence");
        RequireScalarValue(step, "if", "always() && (hashFiles('TestResults/CoverageGate/quotation-complete-coverage.trx') != '' || hashFiles('TestResults/C821Verifier/c821-verifier.trx', 'TestResults/C821Recipient/c821-recipient.trx') != '')");
        RequireScalarValue(step, "uses", "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a");
        var inputs = RequireMapping(step, "with");
        if (inputs.Children.Count != 6)
            throw new InvalidOperationException("Coverage evidence action must have exactly six reviewed inputs.");
        RequireScalarValue(inputs, "name", "quotation-complete-coverage-${{ github.sha }}");
        RequireScalarValue(inputs, "path", "TestResults/C821Verifier/c821-verifier.trx\nTestResults/C821Recipient/c821-recipient.trx\nTestResults/CompanionAdmission/companion-admission.trx\nTestResults/CompanionSigning/companion-signing.trx\nTestResults/StorageFront/storage-front.trx\nTestResults/FinancialCompletion/financial-completion.trx\nTestResults/CoverageGate/quotation-complete-coverage.trx\nTestResults/CoverageGate/*/coverage.cobertura.xml\n");
        RequireScalarValue(inputs, "if-no-files-found", "error");
        RequireScalarValue(inputs, "retention-days", "7");
        RequireScalarValue(inputs, "include-hidden-files", "false");
        RequireScalarValue(inputs, "overwrite", "false");
    }

    private static void ValidateWireEvidenceStep(YamlNode node)
    {
        var step = RequireMapping(node, "wire source evidence step");
        if (!step.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["name", "if", "uses", "with"]))
            throw new InvalidOperationException("Wire evidence step must have only its reviewed keys.");
        RequireScalarValue(step, "name", "Retain actual qualification wire-source evidence");
        RequireScalarValue(step, "if", "always() && hashFiles('TestResults/QualificationWire/qualification-wire.trx') != ''");
        RequireScalarValue(step, "uses", "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a");
        var inputs = RequireMapping(step, "with");
        if (inputs.Children.Count != 6)
            throw new InvalidOperationException("Wire evidence action must have exactly six reviewed inputs.");
        RequireScalarValue(inputs, "name", "qualification-source-wire-${{ github.sha }}");
        RequireScalarValue(inputs, "path", "TestResults/QualificationWire/qualification-wire.trx\nTestResults/QualificationWire/qualification-wire-receipt.json\nTestResults/QualificationWire/empty.json\nTestResults/QualificationWire/mixed.json\n");
        RequireScalarValue(inputs, "if-no-files-found", "error");
        RequireScalarValue(inputs, "retention-days", "7");
        RequireScalarValue(inputs, "include-hidden-files", "false");
        RequireScalarValue(inputs, "overwrite", "false");
    }

    private static void ValidateJoinedAuthorityJob(YamlMappingNode job)
    {
        if (!job.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal)
            .SetEquals(["name", "runs-on", "timeout-minutes", "env", "steps"]))
            throw new InvalidOperationException("Joined authority gate must be unconditional with no permission or failure overrides.");
        RequireScalarValue(job, "name", "qualification-authority-joined");
        RequireScalarValue(job, "runs-on", "ubuntu-latest");
        RequireScalarValue(job, "timeout-minutes", "20");
        ValidateRegistryEnvironment(job, workspace: false);
        var steps = RequireSequence(job, "steps");
        if (steps.Children.Count != 12)
            throw new InvalidOperationException("Joined authority gate requires all original eight steps and four exact registry prerequisite steps.");
        ValidateStep(steps.Children[0], CheckoutAction,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["persist-credentials"] = "false" });
        const string authorityProducerRevision = "8cdb634b3b0abdf18b9b826a0948dbfd98c66ea0";
        var checkouts = new[]
        {
            ("Legacy.Maliev.AuthService", authorityProducerRevision, "TestResults/.joined-auth/Legacy.Maliev.AuthService"),
            ("Legacy.Maliev.ServiceDefaults", "5c5f9479313710fa576f83d3b396442997a2fcf4", "TestResults/.joined-auth/.dependencies/Legacy.Maliev.ServiceDefaults"),
            ("Legacy.Maliev.CompatibilityContracts", "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7", "TestResults/.joined-auth/.dependencies/Legacy.Maliev.CompatibilityContracts"),
            ("Legacy.Maliev.ServiceDefaults", "f72be151917cb3962380971418c187e3df563f8d", "TestResults/.bridge-dependencies/Legacy.Maliev.ServiceDefaults"),
            ("Legacy.Maliev.CompatibilityContracts", "78e48ffc4ee000df0510cba5e7c7a3c4c4d539d7", "TestResults/.bridge-dependencies/Legacy.Maliev.CompatibilityContracts"),
        };
        for (var index = 0; index < checkouts.Length; index++)
        {
            var (repository, revision, path) = checkouts[index];
            ValidateStep(steps.Children[index + 1], CheckoutAction,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["repository"] = "MALIEV-Co-Ltd/" + repository,
                    ["ref"] = revision,
                    ["path"] = path,
                    ["persist-credentials"] = "false",
                });
        }
        ValidateStep(steps.Children[6], "actions/setup-dotnet@a98b56852c35b8e3190ac28c8c2271da59106c68",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["dotnet-version"] = "10.0.x" });
        ValidateRegistrySteps(steps, controls: 7, preload: 8, cleanup: 10, upload: 11);
        ValidateScriptStep(steps.Children[9], "Prove real joined qualification authority",
            "./scripts/run_qualification_authority_joined.ps1 -RepositoryPath $env:GITHUB_WORKSPACE", "pwsh");
    }

    private static void ValidateScriptStep(YamlNode node, string expectedName, string expectedRun, string expectedShell = "bash", string? expectedTimeout = null)
    {
        var step = RequireMapping(node, "workflow script step");
        var actualKeys = step.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal);
        var expectedKeys = new HashSet<string>(["name", "shell", "run"], StringComparer.Ordinal);
        if (expectedTimeout is not null) expectedKeys.Add("timeout-minutes");
        if (!actualKeys.SetEquals(expectedKeys))
        {
            throw new InvalidOperationException("Coverage steps must contain exactly name, shell, and run.");
        }

        RequireScalarValue(step, "name", expectedName);
        RequireScalarValue(step, "shell", expectedShell);
        if (expectedTimeout is not null) RequireScalarValue(step, "timeout-minutes", expectedTimeout);
        if (!string.Equals(RequireScalar(GetRequired(step, "run")).Trim(), expectedRun.Trim(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Coverage step '{expectedName}' changed its reviewed command.");
        }
    }

    private static IReadOnlyList<string> RequireExactReadOnlyPermissions(YamlMappingNode permissions, string scope)
    {
        if (permissions.Children.Count != 1)
        {
            throw new InvalidOperationException($"{scope} permissions must contain only contents: read.");
        }

        RequireScalarValue(permissions, "contents", "read");
        return ["contents:read"];
    }

    private static void ValidateStep(
        YamlNode node,
        string expectedAction,
        IReadOnlyDictionary<string, string> expectedInputs)
    {
        var step = RequireMapping(node, "workflow step");
        var allowedKeys = new HashSet<string>(["name", "uses", "with"], StringComparer.Ordinal);

        var actualKeys = step.Children.Keys.Select(RequireScalar).ToHashSet(StringComparer.Ordinal);
        if (!actualKeys.SetEquals(allowedKeys))
        {
            throw new InvalidOperationException("Each workflow step must contain exactly name, uses, and with.");
        }

        var name = RequireScalar(GetRequired(step, "name"));
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Workflow step name must not be empty.");
        }

        RequireScalarValue(step, "uses", expectedAction);
        var inputs = RequireMapping(step, "with");
        if (inputs.Children.Count != expectedInputs.Count)
        {
            throw new InvalidOperationException($"Action {expectedAction} has an unexpected input count.");
        }

        foreach (var expectedInput in expectedInputs)
        {
            RequireScalarValue(inputs, expectedInput.Key, expectedInput.Value);
        }

        var environment = GetOptional(step, "env");
        if (environment is not null)
        {
            throw new InvalidOperationException("Workflow action steps must not override reserved environment variables.");
        }

        if (expectedInputs.ContainsKey("use-local-maliev-dependencies"))
        {
            var useLocalDependencies = GetRequired(inputs, "use-local-maliev-dependencies") as YamlScalarNode;
            if (useLocalDependencies?.Style != ScalarStyle.SingleQuoted)
            {
                throw new InvalidOperationException("use-local-maliev-dependencies must use the single-quoted string value 'true'.");
            }
        }
    }

    private static void RejectDuplicatedValidationActionsAndCommands(YamlMappingNode jobs)
    {
        foreach (var job in jobs.Children)
        {
            // The separately isolated integration job has its own exact twelve-step contract.
            if (RequireScalar(job.Key) == "qualification-authority-joined") continue;
            if (job.Value is not YamlMappingNode jobNode) continue;
            var stepsNode = GetOptional(jobNode, "steps");
            if (stepsNode is not YamlSequenceNode steps)
            {
                continue;
            }

            foreach (var stepNode in steps.Children.OfType<YamlMappingNode>())
            {
                if (GetOptional(stepNode, "uses") is YamlScalarNode usesNode)
                {
                    var action = usesNode.Value ?? string.Empty;
                    if (action.StartsWith("actions/setup-dotnet@", StringComparison.OrdinalIgnoreCase)
                        || action.StartsWith("actions/cache@", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Caller duplicates shared action {action}.");
                    }
                }

                if (GetOptional(stepNode, "run") is YamlScalarNode runNode
                    && (GetOptional(stepNode, "name") as YamlScalarNode)?.Value is not
                        ("Collect QuotationService coverage" or "Prove actual qualification DTO and controller serializer wire"))
                {
                    RejectDuplicatedDotNetCommand(runNode.Value ?? string.Empty);
                }
            }
        }
    }

    private static void RejectDuplicatedDotNetCommand(string command)
    {
        var tokens = command
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('"', '\'', ';', '&', '|').ToLowerInvariant())
            .Where(token => token.Length > 0)
            .ToArray();

        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!string.Equals(tokens[index], "dotnet", StringComparison.Ordinal))
            {
                continue;
            }

            var verb = tokens[index + 1];
            if (verb is "restore" or "build" or "test" or "format" or "list"
                || tokens[(index + 1)..].Any(token => token is "audit" or "--vulnerable"))
            {
                throw new InvalidOperationException($"Caller duplicates shared dotnet validation command: {verb}.");
            }
        }
    }

    private static YamlMappingNode RequireMapping(YamlMappingNode parent, string key) =>
        RequireMapping(GetRequired(parent, key), key);

    private static YamlMappingNode RequireMapping(YamlNode node, string description)
    {
        return node as YamlMappingNode
            ?? throw new InvalidOperationException($"{description} must be a mapping.");
    }

    private static YamlSequenceNode RequireSequence(YamlMappingNode parent, string key)
    {
        return GetRequired(parent, key) as YamlSequenceNode
            ?? throw new InvalidOperationException($"{key} must be a sequence.");
    }

    private static void RequireScalarValue(YamlMappingNode parent, string key, string expected)
    {
        var actual = RequireScalar(GetRequired(parent, key));
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{key} must equal '{expected}', but was '{actual}'.");
        }
    }

    private static string RequireScalar(YamlNode node)
    {
        return (node as YamlScalarNode)?.Value
            ?? throw new InvalidOperationException("Expected a scalar YAML value.");
    }

    private static YamlNode GetRequired(YamlMappingNode parent, string key)
    {
        return GetOptional(parent, key)
            ?? throw new InvalidOperationException($"Missing required YAML key '{key}'.");
    }

    private static YamlNode? GetOptional(YamlMappingNode parent, string key)
    {
        foreach (var child in parent.Children)
        {
            if (child.Key is YamlScalarNode scalar && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return child.Value;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\$\{\{\s*secrets\s*(?:\.|\[)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretExpression();
}
