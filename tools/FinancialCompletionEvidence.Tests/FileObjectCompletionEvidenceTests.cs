using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace InvoiceCompletionProducerAcceptance;

// Authored pure controls. No SQL, SDK, storage process, scanner, network or provider operation is executed.
public sealed class FileObjectCompletionEvidenceTests
{
    private static readonly byte[] Pdf = "%PDF-1.7\ncontrolled PDF evidence bytes\n%%EOF"u8.ToArray();
    private static string Digest => Convert.ToHexString(SHA256.HashData(Pdf));
    private static AccountingCompletionEvidence Accounting() => new(Digest, "maliev.com", "invoice/part.pdf", default);
    private static FileMetadataSnapshot Snapshot() => new(1, 1, 1, 1,
        [new FileUploadRow(11, "maliev.com", "invoice/part.pdf", "application/pdf", Pdf.Length)],
        [new FileMoveRow(Guid.Parse("619155df-b7f8-4b6c-bcaf-c39fbfc967fe"), true, "maliev.com", "quarantine/random.pdf",
            13, "maliev.com", "invoice/part.pdf", 17, "MetadataCommitted")],
        Persistence("2026-10-07T00:00:00Z", "2026-10-07T00:00:00Z", "2026-10-07T00:00:00Z"));

    private static JsonElement Persistence(string uploadTime, string journalTime, string intentTime) => JsonSerializer.SerializeToElement(new
    {
        Uploads = new[] { new { ID = 11, ModifiedDate = uploadTime } },
        Journals = new[] { new { OperationId = "619155df-b7f8-4b6c-bcaf-c39fbfc967fe", ModifiedAt = journalTime } },
        QuarantineIntents = new[] { new { OperationId = "619155df-b7f8-4b6c-bcaf-c39fbfc967fe", ModifiedAt = intentTime } },
    });

    [Fact]
    public void CleanCommittedCoordinatesReturnTypedIndependentFileOperationEvidence()
    {
        var evidence = FileObjectCompletionReadback.ValidateSnapshot(Snapshot(), Accounting());
        Assert.Equal(11, evidence.Upload.ID);
        Assert.Equal(13, evidence.Move.SourceGeneration);
        Assert.Equal(17, evidence.Move.DestinationGeneration);
        Assert.Equal(Digest, evidence.AccountingPdfSha256);
        Assert.False(evidence.QuarantineAbsenceObserved);
        Assert.NotEqual(Guid.Parse("619155df-b7f8-4b6c-bcaf-c39fbfc967ff"), evidence.Move.OperationId);
    }

    [Theory]
    [InlineData(0, 0, 1, 1)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(2, 1, 1, 1)]
    [InlineData(2, 2, 1, 1)]
    [InlineData(1, 1, 0, 0)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(1, 1, 2, 1)]
    [InlineData(1, 1, 2, 2)]
    public void ExtraOrMissingTotalAndExpectedRowsFail(long uploadTotal, long uploadExpected, long journalTotal, long journalExpected) =>
        Assert.Throws<InvalidDataException>(() => FileObjectCompletionReadback.RequireSingleRows(uploadTotal, uploadExpected, journalTotal, journalExpected));

    [Theory]
    [InlineData("unclean")]
    [InlineData("source-deleted-only")]
    [InlineData("compensated")]
    [InlineData("source-generation")]
    [InlineData("destination-generation")]
    [InlineData("destination-null")]
    [InlineData("source-object")]
    [InlineData("same-coordinates")]
    [InlineData("wrong-destination")]
    [InlineData("wrong-upload")]
    [InlineData("wrong-type")]
    [InlineData("oversized")]
    [InlineData("empty-id")]
    public void InvalidScanStageGenerationOrObjectCoordinatesFail(string fault)
    {
        var snapshot = Snapshot();
        var upload = snapshot.Uploads[0];
        var move = snapshot.Journals[0];
        switch (fault)
        {
            case "unclean": move = move with { ScanClean = false }; break;
            case "source-deleted-only": move = move with { State = "SourceDeleted" }; break;
            case "compensated": move = move with { State = "CompensatedAbsent" }; break;
            case "source-generation": move = move with { SourceGeneration = 0 }; break;
            case "destination-generation": move = move with { DestinationGeneration = -1 }; break;
            case "destination-null": move = move with { DestinationGeneration = null }; break;
            case "source-object": move = move with { SourceObjectName = "" }; break;
            case "same-coordinates": move = move with { SourceObjectName = move.DestinationObjectName }; break;
            case "wrong-destination": move = move with { DestinationObjectName = "invoice/other.pdf" }; break;
            case "wrong-upload": upload = upload with { Name = "invoice/other.pdf" }; break;
            case "wrong-type": upload = upload with { ContentType = "text/html" }; break;
            case "oversized": upload = upload with { Size = 10 * 1024 * 1024 + 1 }; break;
            case "empty-id": move = move with { OperationId = Guid.Empty }; break;
            default: throw new InvalidOperationException();
        }
        Assert.Throws<InvalidDataException>(() => FileObjectCompletionReadback.ValidateSnapshot(
            snapshot with { Uploads = [upload], Journals = [move] }, Accounting()));
    }

    [Fact]
    public void ForgedSingleCountsDoNotAdmitDuplicateActualRows()
    {
        var snapshot = Snapshot();
        Assert.Throws<InvalidDataException>(() => FileObjectCompletionReadback.ValidateSnapshot(
            snapshot with { Journals = [snapshot.Journals[0], snapshot.Journals[0]] }, Accounting()));
    }

    [Fact]
    public void DuplicateSnapshotJsonFieldFailsBeforeDeserialization()
    {
        using var document = JsonDocument.Parse("{\"UploadTotal\":1,\"UploadTotal\":1}");
        Assert.Throws<InvalidDataException>(() => FileObjectCompletionReadback.ParseSnapshot(document.RootElement));
    }

    [Fact]
    public void ExactGenerationUriForOwnedLoopbackOriginIsAdmitted()
    {
        var file = FileObjectCompletionReadback.ValidateSnapshot(Snapshot(), Accounting());
        FileSignedByteReadback.ValidateSignedUri(new Uri("https://127.0.0.1:7102/maliev.com/invoice/part.pdf?generation=17&X-Goog-Signature=controlled"),
            new Uri("https://127.0.0.1:7102/"), file);
    }

    [Fact]
    public void CloudOriginCannotBeSelectedByAcceptanceReadback()
    {
        var file = FileObjectCompletionReadback.ValidateSnapshot(Snapshot(), Accounting());
        Assert.Throws<InvalidDataException>(() => FileSignedByteReadback.ValidateSignedUri(
            new Uri("https://storage.googleapis.com/maliev.com/invoice/part.pdf?generation=17&X-Goog-Signature=controlled"),
            new Uri("https://storage.googleapis.com/"), file));
    }

    [Theory]
    [InlineData("uploads-null")]
    [InlineData("journals-null")]
    [InlineData("upload-null")]
    [InlineData("journal-null")]
    public void MissingCollectionOrNullRowCannotBecomeFileEvidence(string fault)
    {
        var snapshot = Snapshot();
        snapshot = fault switch
        {
            "uploads-null" => snapshot with { Uploads = null! },
            "journals-null" => snapshot with { Journals = null! },
            "upload-null" => snapshot with { Uploads = [null!] },
            "journal-null" => snapshot with { Journals = [null!] },
            _ => throw new InvalidOperationException(),
        };
        Assert.Throws<InvalidDataException>(() => FileObjectCompletionReadback.ValidateSnapshot(snapshot, Accounting()));
    }

    [Theory]
    [InlineData("wrong-generation")]
    [InlineData("leading-zero-generation")]
    [InlineData("duplicate-generation")]
    [InlineData("missing-generation")]
    [InlineData("wrong-object")]
    [InlineData("foreign-origin")]
    [InlineData("unsigned")]
    public void WrongGenerationDuplicateQueryOrEscapedOriginObjectFails(string fault)
    {
        var value = fault switch
        {
            "wrong-generation" => "https://127.0.0.1:7102/maliev.com/invoice/part.pdf?generation=18&X-Goog-Signature=controlled",
            "leading-zero-generation" => "https://127.0.0.1:7102/maliev.com/invoice/part.pdf?generation=017&X-Goog-Signature=controlled",
            "duplicate-generation" => "https://127.0.0.1:7102/maliev.com/invoice/part.pdf?generation=17&generation=17&X-Goog-Signature=controlled",
            "missing-generation" => "https://127.0.0.1:7102/maliev.com/invoice/part.pdf?X-Goog-Signature=controlled",
            "wrong-object" => "https://127.0.0.1:7102/maliev.com/invoice/other.pdf?generation=17&X-Goog-Signature=controlled",
            "foreign-origin" => "https://other.invalid/maliev.com/invoice/part.pdf?generation=17&X-Goog-Signature=controlled",
            "unsigned" => "https://127.0.0.1:7102/maliev.com/invoice/part.pdf?generation=17",
            _ => throw new InvalidOperationException(),
        };
        var file = FileObjectCompletionReadback.ValidateSnapshot(Snapshot(), Accounting());
        Assert.Throws<InvalidDataException>(() => FileSignedByteReadback.ValidateSignedUri(new Uri(value),
            new Uri("https://127.0.0.1:7102/"), file));
    }

    [Fact]
    public async Task ExactBoundedDestinationBytesMatchAccountingDigest()
    {
        var file = FileObjectCompletionReadback.ValidateSnapshot(Snapshot(), Accounting());
        using var body = new MemoryStream(Pdf);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var evidence = await FileSignedByteReadback.VerifyBytesAsync(body, file, DateTimeOffset.UtcNow.AddMinutes(1), deadline.Token);
        Assert.Equal(17, evidence.Generation);
        Assert.Equal(Pdf.Length, evidence.Bytes);
        Assert.Equal(Digest, evidence.Sha256);
        Assert.True(body.CanRead); // Application retains ownership; caller closes it.
    }

    [Theory]
    [InlineData("digest")]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("expired")]
    public async Task ChangedTruncatedExtraOrExpiredDestinationBytesFail(string fault)
    {
        var file = FileObjectCompletionReadback.ValidateSnapshot(Snapshot(), Accounting());
        byte[] bytes = Pdf.ToArray();
        if (fault == "digest") bytes[0] ^= 1;
        if (fault == "short") bytes = bytes[..^1];
        if (fault == "extra") bytes = [.. bytes, 0];
        using var body = new MemoryStream(bytes);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<InvalidDataException>(() => FileSignedByteReadback.VerifyBytesAsync(body, file,
            fault == "expired" ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddMinutes(1), deadline.Token));
    }

    [Fact]
    public async Task CanceledByteReadFailsBeforeConsumingBody()
    {
        var file = FileObjectCompletionReadback.ValidateSnapshot(Snapshot(), Accounting());
        using var body = new MemoryStream(Pdf);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileSignedByteReadback.VerifyBytesAsync(body, file,
            DateTimeOffset.UtcNow.AddMinutes(1), canceled.Token));
        Assert.Equal(0, body.Position);
    }

    [Fact]
    public void StableReplayRetainsActualRowsAndGeneration()
    {
        var file = FileObjectCompletionReadback.ValidateSnapshot(Snapshot(), Accounting());
        FileObjectCompletionReadback.RequireStableReplay(file,
            FileObjectCompletionReadback.ValidateSnapshot(Snapshot(), Accounting()));
    }

    [Fact]
    public void ChangedDestinationGenerationFailsReplayStability()
    {
        var snapshot = Snapshot();
        var file = FileObjectCompletionReadback.ValidateSnapshot(snapshot, Accounting());
        var changed = FileObjectCompletionReadback.ValidateSnapshot(snapshot with
        { Journals = [snapshot.Journals[0] with { DestinationGeneration = 18 }] }, Accounting());
        Assert.Throws<InvalidDataException>(() => FileObjectCompletionReadback.RequireStableReplay(file, changed));
    }

    [Theory]
    [InlineData("upload")]
    [InlineData("journal")]
    [InlineData("quarantine-intent")]
    public void TimestampOnlyPersistenceChangesCannotPassCompletedReplay(string changedOwner)
    {
        const string before = "2026-10-07T00:00:00Z";
        const string after = "2026-10-07T00:00:01Z";
        var snapshot = Snapshot();
        var original = FileObjectCompletionReadback.ValidateSnapshot(snapshot, Accounting());
        var changed = FileObjectCompletionReadback.ValidateSnapshot(snapshot with
        {
            PersistenceSnapshot = Persistence(changedOwner == "upload" ? after : before,
                changedOwner == "journal" ? after : before, changedOwner == "quarantine-intent" ? after : before),
        }, Accounting());
        Assert.Equal(original.Upload, changed.Upload);
        Assert.Equal(original.Move, changed.Move);
        Assert.Throws<InvalidDataException>(() => FileObjectCompletionReadback.RequireStableReplay(original, changed));
    }

    [Fact]
    public void MissingFullPersistenceSnapshotCannotBecomeReplayEvidence()
    {
        Assert.Throws<InvalidDataException>(() => FileObjectCompletionReadback.ValidateSnapshot(
            Snapshot() with { PersistenceSnapshot = default }, Accounting()));
    }

    [Fact]
    public void EmptyQuarantineIntentSetCannotBecomeReplayEvidence()
    {
        var persisted = System.Text.Json.Nodes.JsonNode.Parse(Snapshot().PersistenceSnapshot.GetRawText())!.AsObject();
        persisted["QuarantineIntents"] = new System.Text.Json.Nodes.JsonArray();
        using var parsed = JsonDocument.Parse(persisted.ToJsonString());
        Assert.Throws<InvalidDataException>(() => FileObjectCompletionReadback.ValidateSnapshot(
            Snapshot() with { PersistenceSnapshot = parsed.RootElement }, Accounting()));
    }
}
