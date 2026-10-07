using InvoiceCompletionProducerAcceptance;
using Xunit;

namespace Legacy.Maliev.QuotationService.Tests.Tools;

public sealed class AccountingCompletionFixtureAdmissionTests
{
    private static readonly Uri Document = new("https://127.0.0.1:7101/");
    private static readonly Uri File = new("https://127.0.0.1:7102/");
    private static readonly Uri Notification = new("https://127.0.0.1:7103/");

    [Fact]
    public void ActualConsumedDirectServiceKeysPass()
    {
        AccountingCompletionFixtureAdmission.ValidateOrigins(Environment(), Document, File, Notification);
    }

    [Theory]
    [InlineData("Services__Document")]
    [InlineData("Services__File")]
    [InlineData("Services__Notification")]
    public void BaseUrlSiblingCannotReplaceConsumedDirectKey(string key)
    {
        var environment = Environment();
        var value = environment[key];
        environment.Remove(key);
        environment.Add(key + "__BaseUrl", value);
        Assert.Throws<InvalidDataException>(() => AccountingCompletionFixtureAdmission.ValidateOrigins(environment, Document, File, Notification));
    }

    [Fact]
    public void ActualFileTargetMismatchFails()
    {
        var environment = Environment();
        environment["Services__File"] = "https://foreign.invalid/";
        Assert.Throws<InvalidDataException>(() => AccountingCompletionFixtureAdmission.ValidateOrigins(environment, Document, File, Notification));
    }

    [Fact]
    public void DirectKeyCaseAliasFails()
    {
        var environment = Environment();
        environment.Add("SERVICES__FILE", File.AbsoluteUri);
        Assert.Throws<InvalidDataException>(() => AccountingCompletionFixtureAdmission.ValidateOrigins(environment, Document, File, Notification));
    }

    [Fact]
    public void DirectKeyColonAliasFails()
    {
        var environment = Environment();
        environment.Add("Services:File", File.AbsoluteUri);
        Assert.Throws<InvalidDataException>(() => AccountingCompletionFixtureAdmission.ValidateOrigins(environment, Document, File, Notification));
    }

    [Fact]
    public void DeclaredDedicatedFileHttpOriginFitsTheSevenHostHttpsGraph()
    {
        AccountingCompletionFixtureAdmission.ValidateTargetOrigins(Document, new Uri("http://127.0.0.1:7102/"), Notification);
    }

    [Theory]
    [InlineData("document-http")]
    [InlineData("file-https")]
    [InlineData("notification-http")]
    [InlineData("file-hostname")]
    [InlineData("file-cloud")]
    [InlineData("file-query")]
    [InlineData("file-low-port")]
    public void AlternateTransportOrUnownedFileTargetsFail(string fault)
    {
        var document = fault == "document-http" ? new Uri("http://127.0.0.1:7101/") : Document;
        var notification = fault == "notification-http" ? new Uri("http://127.0.0.1:7103/") : Notification;
        var file = fault switch
        {
            "file-https" => File,
            "file-hostname" => new Uri("http://localhost:7102/"),
            "file-cloud" => new Uri("http://storage.googleapis.com:7102/"),
            "file-query" => new Uri("http://127.0.0.1:7102/?other=1"),
            "file-low-port" => new Uri("http://127.0.0.1:80/"),
            _ => new Uri("http://127.0.0.1:7102/"),
        };
        Assert.Throws<InvalidDataException>(() => AccountingCompletionFixtureAdmission.ValidateTargetOrigins(document, file, notification));
    }

    private static Dictionary<string, string> Environment() => new(StringComparer.Ordinal)
    {
        ["Services__Document"] = Document.AbsoluteUri,
        ["Services__File"] = File.AbsoluteUri,
        ["Services__Notification"] = Notification.AbsoluteUri,
    };
}
