using System.Xml.Linq;

namespace Legacy.Maliev.QuotationService.Tests.Diagnostics;

/// <summary>Reads real SDK outputs rather than treating source flags or deleted artifacts as emission proof.</summary>
public sealed class GeneratedDocumentationEmissionTests
{
    [Theory]
    [InlineData("Api")]
    [InlineData("Application")]
    [InlineData("Data")]
    [InlineData("Domain")]
    [InlineData("MigrationRunner")]
    [InlineData("Tests")]
    public void Release_build_emits_owned_documentation_in_output_directory(string component)
    {
        var root = FindRepository();
        var name = "Legacy.Maliev.QuotationService." + component;
        var project = Path.Combine(root, name);
        var output = Path.Combine(project, "bin", "Release", "net10.0", name + ".xml");
        Assert.True(File.Exists(output), $"The actual Release build did not emit {name} XML documentation.");
        var document = XDocument.Load(output);
        Assert.Equal("doc", document.Root!.Name.LocalName);
        Assert.Equal(name, document.Root.Element("assembly")!.Element("name")!.Value);
        Assert.NotNull(document.Root.Element("members"));
        Assert.False(File.Exists(Path.Combine(project, name + ".xml")),
            "Generated documentation must remain isolated from source-root artifacts.");
    }

    private static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Legacy.Maliev.QuotationService.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Quotation repository was not found.");
    }
}
