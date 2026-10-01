namespace Legacy.Maliev.QuotationService.Tests.Workflows;

/// <summary>Portable actual-consumer tests require an unconditional immutable private checkout.</summary>
public sealed class InvoiceConsumerWorkflowContractTests
{
    [Theory]
    [InlineData("floating-ref")]
    [InlineData("wrong-path")]
    [InlineData("persist-credentials")]
    [InlineData("conditional")]
    [InlineData("failure-override")]
    [InlineData("missing")]
    public void Accounting_consumer_dependency_cannot_float_skip_fail_open_or_retain_credentials(string mutant)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".github", "workflows", "_build-and-test.yml"))) root = root.Parent;
        Assert.NotNull(root);
        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "_build-and-test.yml")).Replace("\r\n", "\n", StringComparison.Ordinal);
        WorkflowContractValidator.Validate(workflow);
        const string step = """
              - name: Check out exact Accounting consumer test dependency
                uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1
                with:
                  repository: MALIEV-Co-Ltd/Legacy.Maliev.AccountingService
                  ref: ae0826156b06c34476e95de8c53dfccfcf5a5972
                  path: .dependencies/Legacy.Maliev.AccountingService
                  persist-credentials: false
        """;
        Assert.Contains(step, workflow, StringComparison.Ordinal);
        var replacement = mutant switch
        {
            "floating-ref" => step.Replace("ref: ae0826156b06c34476e95de8c53dfccfcf5a5972", "ref: main", StringComparison.Ordinal),
            "wrong-path" => step.Replace("path: .dependencies/Legacy.Maliev.AccountingService", "path: .dependencies/elsewhere", StringComparison.Ordinal),
            "persist-credentials" => step.Replace("persist-credentials: false", "persist-credentials: true", StringComparison.Ordinal),
            "conditional" => step.Replace("        uses:", "        if: false\n        uses:", StringComparison.Ordinal),
            "failure-override" => step.Replace("        uses:", "        continue-on-error: true\n        uses:", StringComparison.Ordinal),
            "missing" => string.Empty,
            _ => throw new ArgumentOutOfRangeException(nameof(mutant)),
        };
        var changed = workflow.Replace(step, replacement, StringComparison.Ordinal);
        Assert.NotEqual(workflow, changed);
        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(changed));
    }
}
