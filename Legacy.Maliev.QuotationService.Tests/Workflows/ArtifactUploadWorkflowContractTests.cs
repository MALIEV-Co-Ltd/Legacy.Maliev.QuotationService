namespace Legacy.Maliev.QuotationService.Tests.Workflows;

/// <summary>Each evidence upload must use the exact reviewed action revision.</summary>
public sealed class ArtifactUploadWorkflowContractTests
{
    [Theory]
    [InlineData("Retain complete coverage evidence", "floating")]
    [InlineData("Retain complete coverage evidence", "stale")]
    [InlineData("Retain complete coverage evidence", "unreviewed")]
    [InlineData("Retain complete coverage evidence", "commented-pin")]
    [InlineData("Retain actual qualification wire-source evidence", "floating")]
    [InlineData("Retain actual qualification wire-source evidence", "stale")]
    [InlineData("Retain actual qualification wire-source evidence", "unreviewed")]
    [InlineData("Retain actual qualification wire-source evidence", "commented-pin")]
    public void EvidenceUpload_RejectsAnyRevisionOtherThanReviewedPin(string stepName, string mutant)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, ".github", "workflows", "_build-and-test.yml")))
            root = root.Parent;
        Assert.NotNull(root);
        var workflow = File.ReadAllText(Path.Combine(root.FullName, ".github", "workflows", "_build-and-test.yml"));
        WorkflowContractValidator.Validate(workflow);
        const string action = "actions/upload-artifact@043fb46d1a93c77aae656e7c1c64a875d1fc6a0a";
        var stepIndex = workflow.IndexOf("- name: " + stepName, StringComparison.Ordinal);
        Assert.True(stepIndex >= 0);
        var actionIndex = workflow.IndexOf(action, stepIndex, StringComparison.Ordinal);
        Assert.True(actionIndex >= 0);
        var replacement = mutant switch
        {
            "floating" => "actions/upload-artifact@v7",
            "stale" => "actions/upload-artifact@ea165f8d65b6e75b540449e92b4886f43607fa02",
            "unreviewed" => "actions/upload-artifact@0000000000000000000000000000000000000000",
            "commented-pin" => "actions/upload-artifact@main # 043fb46d1a93c77aae656e7c1c64a875d1fc6a0a",
            _ => throw new ArgumentOutOfRangeException(nameof(mutant)),
        };
        var changed = workflow[..actionIndex] + replacement + workflow[(actionIndex + action.Length)..];
        Assert.NotEqual(workflow, changed);
        Assert.Throws<InvalidOperationException>(() => WorkflowContractValidator.Validate(changed));
    }
}
