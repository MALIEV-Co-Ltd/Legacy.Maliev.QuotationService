using YamlDotNet.RepresentationModel;

namespace Legacy.Maliev.QuotationService.Tests;

public sealed class DeliveryContractTests
{
    private const string RuntimeSecret = "legacy-maliev-quotation-runtime";

    [Fact]
    public void BaseResources_AreDormantInternalMalievLegacyTemplates()
    {
        var deployment = Document("deployment.yaml");
        Assert.Equal("Deployment", Scalar(deployment, "kind"));
        Assert.Equal("maliev-legacy", Scalar(Map(deployment, "metadata"), "namespace"));
        Assert.Equal("1", Scalar(Map(deployment, "spec"), "replicas"));

        var service = Document("service.yaml");
        Assert.Equal("Service", Scalar(service, "kind"));
        Assert.Equal("maliev-legacy", Scalar(Map(service, "metadata"), "namespace"));
        Assert.Equal("ClusterIP", Scalar(Map(service, "spec"), "type"));

        var resources = KustomizationResources();
        Assert.Equal(
            ["deployment.yaml", "network-policy.yaml", "service-account.yaml", "service.yaml"],
            resources.Order(StringComparer.Ordinal).ToArray());
        Assert.False(Directory.EnumerateFiles(DeployRoot(), "*ingress*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void Deployment_UsesImmutableImageAndExactRuntimeProjection()
    {
        var container = ApiContainer();
        var image = Scalar(container, "image");
        Assert.Matches(@"^[^\s@:]+(?:/[^\s@:]+)+@sha256:[0-9a-f]{64}$", image);
        Assert.StartsWith("registry.invalid/", image, StringComparison.Ordinal);
        Assert.DoesNotContain(":latest", image, StringComparison.OrdinalIgnoreCase);

        var envFrom = Sequence(container, "envFrom");
        var secretReferences = envFrom.Children
            .Select(node => Scalar(Map(Mapping(node), "secretRef"), "name"))
            .ToArray();
        Assert.Equal([RuntimeSecret], secretReferences);
        var deploymentText = Read("base", "deployment.yaml");
        Assert.Equal(1, Count(deploymentText, "secretRef:"));
        Assert.DoesNotContain("secretKeyRef:", deploymentText, StringComparison.Ordinal);

        var env = Environment(container);
        Assert.Equal(
            new[]
            {
                "Cache__AllowInMemoryFallback", "Cache__RedisEnabled", "DOTNET_GCConserveMemory",
                "DOTNET_GCHeapHardLimit", "Features__AllowExactServiceClaimsForLiveCheck",
                "NPGSQL_GSSAPI_AUTHENTICATION", "PGGSSENCMODE", "ServiceAuthentication__ClientId",
                "Services__Auth", "Services__Order", "TMPDIR",
            },
            env.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("legacy-quotation", env["ServiceAuthentication__ClientId"]);
        Assert.Equal("http://legacy-maliev-auth-service", env["Services__Auth"]);
        Assert.Equal("http://legacy-maliev-order-service", env["Services__Order"]);
        Assert.Equal("true", env["Cache__RedisEnabled"]);
        Assert.Equal("false", env["Cache__AllowInMemoryFallback"]);
        Assert.Equal("false", env["Features__AllowExactServiceClaimsForLiveCheck"]);
        Assert.Equal("134217728", env["DOTNET_GCHeapHardLimit"]);
        Assert.Equal("3", env["DOTNET_GCConserveMemory"]);
        Assert.Equal("false", env["NPGSQL_GSSAPI_AUTHENTICATION"]);
        Assert.Equal("disable", env["PGGSSENCMODE"]);

        var readme = Read("README.md");
        foreach (var projectedName in new[]
        {
            "ConnectionStrings__QuotationDbContext",
            "ConnectionStrings__QuotationRequestDbContext",
            "ConnectionStrings__redis",
            "Jwt__PublicKey",
            "Jwt__Issuer",
            "Jwt__Audience",
            "ServiceAuthentication__ClientSecret",
        })
        {
            Assert.Contains($"`{projectedName}`", readme, StringComparison.Ordinal);
        }

        Assert.Contains("`maliev-legacy-secrets`", readme, StringComparison.Ordinal);
        Assert.Contains($"`{RuntimeSecret}`", readme, StringComparison.Ordinal);
    }

    [Fact]
    public void Deployment_HasExactHealthResourceAndRolloutContract()
    {
        var deployment = Document("deployment.yaml");
        var spec = Map(deployment, "spec");
        var strategy = Map(spec, "strategy");
        Assert.Equal("RollingUpdate", Scalar(strategy, "type"));
        var rolling = Map(strategy, "rollingUpdate");
        Assert.Equal("0", Scalar(rolling, "maxSurge"));
        Assert.Equal("1", Scalar(rolling, "maxUnavailable"));

        var container = ApiContainer();
        AssertProbe(container, "startupProbe", "/quotation/liveness");
        AssertProbe(container, "livenessProbe", "/quotation/liveness");
        AssertProbe(container, "readinessProbe", "/quotation/readiness");

        var resources = Map(container, "resources");
        AssertResources(Map(resources, "requests"), "50m", "96Mi");
        AssertResources(Map(resources, "limits"), "300m", "256Mi");
    }

    [Fact]
    public void Workload_IsTokenlessNonRootAndCapabilityFree()
    {
        var deployment = Document("deployment.yaml");
        var podSpec = Map(Map(Map(deployment, "spec"), "template"), "spec");
        Assert.Equal("legacy-maliev-quotation", Scalar(podSpec, "serviceAccountName"));
        Assert.Equal("false", Scalar(podSpec, "automountServiceAccountToken"));

        var podSecurity = Map(podSpec, "securityContext");
        Assert.Equal("true", Scalar(podSecurity, "runAsNonRoot"));
        Assert.True(int.Parse(Scalar(podSecurity, "runAsUser"), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.True(int.Parse(Scalar(podSecurity, "runAsGroup"), System.Globalization.CultureInfo.InvariantCulture) > 0);
        Assert.Equal("RuntimeDefault", Scalar(Map(podSecurity, "seccompProfile"), "type"));

        var containerSecurity = Map(ApiContainer(), "securityContext");
        Assert.Equal("false", Scalar(containerSecurity, "allowPrivilegeEscalation"));
        Assert.Equal("true", Scalar(containerSecurity, "readOnlyRootFilesystem"));
        Assert.Equal(["ALL"], Sequence(Map(containerSecurity, "capabilities"), "drop").Children.Select(Scalar).ToArray());

        var serviceAccount = Document("service-account.yaml");
        Assert.Equal("legacy-maliev-quotation", Scalar(Map(serviceAccount, "metadata"), "name"));
        Assert.Equal("false", Scalar(serviceAccount, "automountServiceAccountToken"));
        Assert.DoesNotContain(Map(serviceAccount, "metadata").Children,
            entry => Scalar(entry.Key).Contains("gcp-service-account", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NetworkPolicy_AllowsOnlyRequiredProtocolSurfaces()
    {
        var policy = Document("network-policy.yaml");
        Assert.Equal("NetworkPolicy", Scalar(policy, "kind"));
        Assert.Equal("maliev-legacy", Scalar(Map(policy, "metadata"), "namespace"));
        var text = Read("base", "network-policy.yaml");
        var spec = Map(policy, "spec");
        Assert.Equal(["Ingress", "Egress"], Sequence(spec, "policyTypes").Children.Select(Scalar).ToArray());

        Assert.Contains("kubernetes.io/metadata.name: maliev-legacy", text, StringComparison.Ordinal);
        Assert.Contains("kubernetes.io/metadata.name: kube-system", text, StringComparison.Ordinal);
        Assert.Contains("app.kubernetes.io/name: legacy-redis", text, StringComparison.Ordinal);
        Assert.Contains("app.kubernetes.io/name: legacy-maliev-auth-service", text, StringComparison.Ordinal);
        Assert.Contains("app.kubernetes.io/name: legacy-maliev-order-service", text, StringComparison.Ordinal);
        foreach (var port in new[] { "53", "5432", "6379", "8080" })
        {
            Assert.Contains($"port: {port}", text, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("0.0.0.0/0", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ipBlock:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("cloudnative-pg.io/cluster", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cnpg.io/cluster", text, StringComparison.OrdinalIgnoreCase);

        var ports = Descendants(policy)
            .OfType<YamlMappingNode>()
            .SelectMany(mapping => mapping.Children)
            .Where(entry => string.Equals(Scalar(entry.Key), "port", StringComparison.Ordinal))
            .Select(entry => Scalar(entry.Value))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["53", "5432", "6379", "80", "8080"], ports);
    }

    [Fact]
    public void Templates_ExcludeForbiddenLegacyAndCredentialSurfaces()
    {
        var text = string.Join('\n', Directory.EnumerateFiles(DeployRoot(), "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("kind: Secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stringData:", text, StringComparison.OrdinalIgnoreCase);
        foreach (var forbidden in new[]
        {
            "SqlServer", "LogDbContext", "Jwt__PrivateKey", "Jwt__Key", "Jwt__Secret",
            "MeasurementProtocol", "MeasurementId", "ApiSecret", "GOOGLE_APPLICATION_CREDENTIALS",
            "service-account.json", "gcp-service-account", ":latest", "kubectl apply", "helm install",
        })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Readme_FreezesCentralGitOpsAndReleaseGates()
    {
        var readme = Read("README.md");
        foreach (var required in new[]
        {
            "dormant", "central GitOps", "immutable digest", "DataMigration", "receipt", "snapshot",
            "capacity", "Aspire", "owner approval", "no new node pool", "CloudNativePG", "PgBouncer",
            "kubelet", "no direct deployment",
        })
        {
            Assert.Contains(required, readme, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void ServiceWorkflows_NeverDeployDirectly()
    {
        var workflows = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), ".github", "workflows"), "*.yml");
        foreach (var workflow in workflows)
        {
            var text = File.ReadAllText(workflow);
            Assert.DoesNotContain("kubectl", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("helm ", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gcloud container clusters", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertProbe(YamlMappingNode container, string name, string path)
    {
        var probe = Map(container, name);
        var httpGet = Map(probe, "httpGet");
        Assert.Equal(path, Scalar(httpGet, "path"));
        Assert.Equal("http", Scalar(httpGet, "port"));
    }

    private static void AssertResources(YamlMappingNode resources, string cpu, string memory)
    {
        Assert.Equal(cpu, Scalar(resources, "cpu"));
        Assert.Equal(memory, Scalar(resources, "memory"));
    }

    private static IReadOnlyDictionary<string, string> Environment(YamlMappingNode container) =>
        Sequence(container, "env").Children
            .Select(Mapping)
            .ToDictionary(node => Scalar(node, "name"), node => Scalar(node, "value"), StringComparer.Ordinal);

    private static YamlMappingNode ApiContainer()
    {
        var deployment = Document("deployment.yaml");
        var podSpec = Map(Map(Map(deployment, "spec"), "template"), "spec");
        return Sequence(podSpec, "containers").Children.Select(Mapping)
            .Single(container => string.Equals(Scalar(container, "name"), "quotation", StringComparison.Ordinal));
    }

    private static string[] KustomizationResources() => Sequence(Document("kustomization.yaml"), "resources")
        .Children.Select(Scalar).ToArray();

    private static YamlMappingNode Document(string file)
    {
        var yaml = new YamlStream();
        yaml.Load(new StringReader(Read("base", file)));
        Assert.Single(yaml.Documents);
        return Mapping(yaml.Documents[0].RootNode);
    }

    private static YamlMappingNode Map(YamlMappingNode parent, string key) => Mapping(Node(parent, key));
    private static YamlMappingNode Mapping(YamlNode node) => Assert.IsType<YamlMappingNode>(node);
    private static YamlSequenceNode Sequence(YamlMappingNode parent, string key) => Assert.IsType<YamlSequenceNode>(Node(parent, key));
    private static string Scalar(YamlMappingNode parent, string key) => Scalar(Node(parent, key));
    private static string Scalar(YamlNode node) => Assert.IsType<YamlScalarNode>(node).Value ?? string.Empty;

    private static IEnumerable<YamlNode> Descendants(YamlNode node)
    {
        yield return node;
        if (node is YamlMappingNode mapping)
        {
            foreach (var child in mapping.Children)
            {
                foreach (var descendant in Descendants(child.Value))
                {
                    yield return descendant;
                }
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            foreach (var child in sequence.Children)
            {
                foreach (var descendant in Descendants(child))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static YamlNode Node(YamlMappingNode parent, string key) => parent.Children
        .Single(entry => string.Equals(Scalar(entry.Key), key, StringComparison.Ordinal)).Value;

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([DeployRoot(), .. parts]));
    private static int Count(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;
    private static string DeployRoot() => Path.Combine(RepositoryRoot(), "deploy");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.QuotationService.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("QuotationService repository root not found.");
    }
}
